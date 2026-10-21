using System.Collections.Generic;
using System.Text;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	// Diagnostics only: captures finished storage transfer routes before StorageTransferSystem consumes them, and
	// splits them into sections the same way StorageTransferSystem.HandleCargoPath does (a new section starts at
	// every storage company the route passes). Used to find out why goods go by truck between cargo stations.
	public partial class RouteCaptureDiagnosticsSystem : GameSystemBase
	{
		private const int kMaxSamplesPerReport = 15;
		private const uint kReportFrames = 4096;
		private const int kMaxHopsPerReport = 25;
		private const int kMaxElementDumpsPerReport = 8;

		private struct Section
		{
			public Entity From;
			public Entity To;
			public string Mode;
			public float RoadMetres;
			public float4 RoadCosts;
			public int FirstElement;
			public int LastElement;
		}

		private EntityQuery m_Query;
		private SimulationSystem m_SimulationSystem;
		private PrefabSystem m_PrefabSystem;

		private readonly Dictionary<Entity, RouteResultKey> m_Seen = new Dictionary<Entity, RouteResultKey>();
		private readonly Dictionary<string, int> m_PatternCounts = new Dictionary<string, int>();
		private readonly List<string> m_Samples = new List<string>();
		private readonly List<string> m_Hops = new List<string>();
		private List<CargoLine> m_Lines;
		private int m_LayoutReports;
		private int m_RideThroughTargets;
		private int m_RideThroughOther;
		private EntityQuery m_LineQuery;
		private ResourceSystem m_ResourceSystem;
		private int m_Paths;
		private int m_PathsWithRoadBetweenStations;
		private uint m_LastReport;

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();
			m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
			m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
			m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
			m_LineQuery = GetEntityQuery(
				ComponentType.ReadOnly<TransportLine>(),
				ComponentType.ReadOnly<RouteWaypoint>(),
				ComponentType.ReadOnly<RouteSegment>(),
				ComponentType.ReadOnly<PrefabRef>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
			m_Query = GetEntityQuery(
				ComponentType.ReadOnly<StorageTransfer>(),
				ComponentType.ReadOnly<PathInformation>(),
				ComponentType.ReadOnly<PathElement>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
		}

		[Preserve]
		protected override void OnUpdate()
		{
			if (!Mod.EnableDiagnostics)
			{
				return;
			}

			NativeArray<Entity> entities = m_Query.ToEntityArray(Allocator.Temp);
			for (int i = 0; i < entities.Length; i++)
			{
				PathInformation info = EntityManager.GetComponentData<PathInformation>(entities[i]);
				if ((info.m_State & PathFlags.Pending) != 0)
				{
					// A new search is running for this storage; allow it to be captured again when done.
					m_Seen.Remove(entities[i]);
					continue;
				}

				RouteResultKey key = RouteResultKey.Of(info);
				if (m_Seen.TryGetValue(entities[i], out RouteResultKey seen) && seen.Equals(key))
				{
					continue;
				}

				m_Seen[entities[i]] = key;

				try
				{
					Capture(entities[i], info);
				}
				catch (System.Exception ex)
				{
					Mod.log.Info("[Diag] RouteCapture failed: " + ex.Message);
				}
			}

			entities.Dispose();

			uint frame = m_SimulationSystem.frameIndex;
			if (frame - m_LastReport >= kReportFrames)
			{
				m_LastReport = frame;
				Report();
			}
		}

		private void Capture(Entity storage, PathInformation info)
		{
			StorageTransfer transfer = EntityManager.GetComponentData<StorageTransfer>(storage);
			DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(storage, true);
			if (path.Length == 0 || info.m_Origin == Entity.Null || info.m_Destination == Entity.Null)
			{
				return;
			}

			List<Section> sections = Split(info, path);
			CountRideThrough(path, transfer);
			m_Paths++;

			StringBuilder pattern = new StringBuilder();
			StringBuilder route = new StringBuilder();
			bool roadBetweenStations = false;
			for (int s = 0; s < sections.Count; s++)
			{
				Section section = sections[s];
				if (s == 0)
				{
					pattern.Append(Kind(section.From));
					route.Append('[').Append(Describe(section.From)).Append(']');
				}

				pattern.Append(" -").Append(section.Mode).Append("-> ").Append(Kind(section.To));
				route.Append(" -").Append(section.Mode).Append("-> [").Append(Describe(section.To)).Append(']');
				if (section.Mode == "truck" && IsStation(section.From) && IsStation(section.To))
				{
					roadBetweenStations = true;
					if (m_Hops.Count < kMaxHopsPerReport)
					{
						m_Hops.Add(CompareHop(transfer, section));
						if (m_Hops.Count <= kMaxElementDumpsPerReport)
						{
							m_Hops.Add("  elements: " + DumpElements(path, section.FirstElement - 6, section.LastElement + 6, section.FirstElement, section.LastElement));
						}
					}
				}
			}

			string key = pattern.ToString();
			m_PatternCounts.TryGetValue(key, out int count);
			m_PatternCounts[key] = count + 1;

			if (roadBetweenStations)
			{
				m_PathsWithRoadBetweenStations++;
				if (m_Samples.Count < kMaxSamplesPerReport)
				{
					m_Samples.Add($"{transfer.m_Resource} {math.abs(transfer.m_Amount)}: {route} | cost={info.m_TotalCost:0.0} dist={info.m_Distance / 1000f:0.0}km duration={info.m_Duration:0}s");
				}
			}
		}

		// Mirrors StorageTransferSystem.HandleCargoPath: modes accumulate until the route enters another storage
		// company (via its pedestrian/cargo lane or a line stop), which closes the current section.
		private List<Section> Split(PathInformation info, DynamicBuffer<PathElement> path)
		{
			List<Section> sections = new List<Section>();
			Entity current = info.m_Origin;
			bool car = false;
			bool track = false;
			string line = null;
			float road = 0f;
			float4 roadCosts = float4.zero;
			int first = 0;

			for (int i = 0; i < path.Length; i++)
			{
				Entity target = path[i].m_Target;
				if (EntityManager.HasComponent<Game.Net.CarLane>(target))
				{
					car = true;
					if (EntityManager.HasComponent<Curve>(target))
					{
						road += EntityManager.GetComponentData<Curve>(target).m_Length * math.abs(path[i].m_TargetDelta.y - path[i].m_TargetDelta.x);
						roadCosts += LaneCosts(target, path[i]);
					}
				}
				else if (EntityManager.HasComponent<Game.Net.TrackLane>(target))
				{
					track = true;
				}
				else if (EntityManager.HasComponent<Game.Net.PedestrianLane>(target))
				{
					Entity company = StorageCompanyFromLane(target);
					if (company != Entity.Null && company != current)
					{
						sections.Add(new Section { From = current, To = company, Mode = Mode(car, track, line), RoadMetres = road, RoadCosts = roadCosts, FirstElement = first, LastElement = i });
						current = company;
						first = i;
						car = track = false;
						road = 0f;
						roadCosts = float4.zero;
						line = null;
					}
				}
				else if (EntityManager.HasComponent<Game.Net.ConnectionLane>(target))
				{
					Game.Net.ConnectionLane lane = EntityManager.GetComponentData<Game.Net.ConnectionLane>(target);
					if ((lane.m_Flags & ConnectionLaneFlags.Road) != 0)
					{
						car = true;
					}
					else if ((lane.m_Flags & ConnectionLaneFlags.Track) != 0)
					{
						track = true;
					}
				}
				else if (EntityManager.HasComponent<Game.Routes.Segment>(target))
				{
					StorageCompaniesFromSegment(target, out Entity start, out Entity end);
					if (start != Entity.Null && start != current)
					{
						sections.Add(new Section { From = current, To = start, Mode = Mode(car, track, line), RoadMetres = road, RoadCosts = roadCosts, FirstElement = first, LastElement = i });
						current = start;
						first = i;
						car = track = false;
						road = 0f;
						roadCosts = float4.zero;
						line = null;
					}

					line = LineName(target);
					if (end != Entity.Null && end != current)
					{
						sections.Add(new Section { From = current, To = end, Mode = Mode(car, track, line), RoadMetres = road, RoadCosts = roadCosts, FirstElement = first, LastElement = i });
						current = end;
						first = i;
						car = track = false;
						road = 0f;
						roadCosts = float4.zero;
						line = null;
					}
				}
			}

			if (info.m_Destination != current)
			{
				sections.Add(new Section { From = current, To = info.m_Destination, Mode = Mode(car, track, line), RoadMetres = road, RoadCosts = roadCosts, FirstElement = first, LastElement = path.Length - 1 });
			}

			return sections;
		}

		// Driving part of PathUtils.GetCarDriveSpecification + PathfindJobs.CalculateCost for the driven fraction of
		// one car lane: prefab driving cost per metre plus time at the (flow-reduced) speed limit. Turning, lane
		// crossing and curve costs are left out. x = time, y = behaviour, z = money, w = comfort (unweighted).
		private float4 LaneCosts(Entity lane, PathElement element)
		{
			float length = EntityManager.GetComponentData<Curve>(lane).m_Length * math.abs(element.m_TargetDelta.y - element.m_TargetDelta.x);
			Game.Net.CarLane carLane = EntityManager.GetComponentData<Game.Net.CarLane>(lane);
			float speed = math.min(111.111115f, carLane.m_SpeedLimit);
			speed = math.max(1f, speed - carLane.m_FlowOffset * (1f / 256f) * speed);
			float4 costs = new float4(length / speed, 0f, 0f, 0f);
			if (EntityManager.HasComponent<PrefabRef>(lane))
			{
				Entity prefab = EntityManager.GetComponentData<PrefabRef>(lane).m_Prefab;
				if (EntityManager.HasComponent<NetLaneData>(prefab))
				{
					Entity pathfind = EntityManager.GetComponentData<NetLaneData>(prefab).m_PathfindPrefab;
					if (EntityManager.HasComponent<PathfindCarData>(pathfind))
					{
						costs += EntityManager.GetComponentData<PathfindCarData>(pathfind).m_DrivingCost.m_Value * length;
					}
				}
			}

			return costs;
		}

		// One token per path element, consecutive car lanes collapsed. Brackets mark the section's own range.
		private string DumpElements(DynamicBuffer<PathElement> path, int from, int to, int sectionFirst, int sectionLast)
		{
			from = math.max(0, from);
			to = math.min(path.Length - 1, to);
			StringBuilder sb = new StringBuilder();
			int carRun = 0;
			for (int i = from; i <= to; i++)
			{
				Entity target = path[i].m_Target;
				bool isCar = EntityManager.HasComponent<Game.Net.CarLane>(target);
				if (isCar && i != sectionFirst && i != sectionLast)
				{
					carRun++;
					continue;
				}

				if (carRun > 0)
				{
					sb.Append("car x").Append(carRun).Append(' ');
					carRun = 0;
				}

				if (i == sectionFirst)
				{
					sb.Append("[[ ");
				}

				sb.Append(ElementToken(target, path[i])).Append(' ');
				if (i == sectionLast)
				{
					sb.Append("]] ");
				}
			}

			if (carRun > 0)
			{
				sb.Append("car x").Append(carRun);
			}

			return sb.ToString();
		}

		private string ElementToken(Entity target, PathElement element)
		{
			string kind;
			if (EntityManager.HasComponent<Game.Net.CarLane>(target))
			{
				kind = "car";
			}
			else if (EntityManager.HasComponent<Game.Net.TrackLane>(target))
			{
				kind = "track";
			}
			else if (EntityManager.HasComponent<Game.Net.PedestrianLane>(target))
			{
				kind = "ped";
			}
			else if (EntityManager.HasComponent<Game.Net.ConnectionLane>(target))
			{
				kind = "conn(" + EntityManager.GetComponentData<Game.Net.ConnectionLane>(target).m_Flags.ToString().Replace(", ", "+") + ")";
			}
			else if (EntityManager.HasComponent<Game.Routes.Segment>(target))
			{
				kind = "seg(" + LineName(target) + " i" + EntityManager.GetComponentData<Game.Routes.Segment>(target).m_Index + ")";
			}
			else if (EntityManager.HasComponent<Game.Routes.Waypoint>(target))
			{
				kind = "waypoint";
			}
			else if (EntityManager.HasComponent<Game.Routes.TransportStop>(target))
			{
				kind = "stop";
			}
			else
			{
				kind = "other";
			}

			Entity owner = target;
			while (EntityManager.HasComponent<Owner>(owner))
			{
				owner = EntityManager.GetComponentData<Owner>(owner).m_Owner;
				if (StorageCompanyOf(owner) != Entity.Null || EntityManager.HasComponent<Game.Objects.OutsideConnection>(owner))
				{
					break;
				}
			}

			string ownerName = owner != target && EntityManager.HasComponent<PrefabRef>(owner) ? "@" + m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(owner).m_Prefab) + "#" + owner.Index : "";
			return $"{kind}{ownerName}({element.m_TargetDelta.x:0.##}-{element.m_TargetDelta.y:0.##})";
		}

		private static string Mode(bool car, bool track, string line)
		{
			if (line != null)
			{
				return car ? "truck+" + line : line;
			}

			if (car && track)
			{
				return "truck+track";
			}

			return car ? "truck" : (track ? "track" : "none");
		}

		private string LineName(Entity segment)
		{
			if (!EntityManager.HasComponent<Owner>(segment))
			{
				return "line";
			}

			Entity route = EntityManager.GetComponentData<Owner>(segment).m_Owner;
			if (EntityManager.HasComponent<PrefabRef>(route))
			{
				Entity prefab = EntityManager.GetComponentData<PrefabRef>(route).m_Prefab;
				if (EntityManager.HasComponent<TransportLineData>(prefab))
				{
					return EntityManager.GetComponentData<TransportLineData>(prefab).m_TransportType.ToString().ToLowerInvariant() + "-line#" + route.Index;
				}
			}

			return "line#" + route.Index;
		}

		private Entity StorageCompanyFromLane(Entity entity)
		{
			while (EntityManager.HasComponent<Owner>(entity))
			{
				entity = EntityManager.GetComponentData<Owner>(entity).m_Owner;
				Entity company = StorageCompanyOf(entity);
				if (company != Entity.Null)
				{
					return company;
				}
			}

			return Entity.Null;
		}

		private Entity StorageCompanyFromWaypoint(Entity waypoint)
		{
			if (!EntityManager.HasComponent<Connected>(waypoint))
			{
				return Entity.Null;
			}

			Entity entity = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
			while (true)
			{
				Entity company = StorageCompanyOf(entity);
				if (company != Entity.Null)
				{
					return company;
				}

				if (!EntityManager.HasComponent<Owner>(entity))
				{
					return Entity.Null;
				}

				entity = EntityManager.GetComponentData<Owner>(entity).m_Owner;
			}
		}

		private Entity StorageCompanyOf(Entity entity)
		{
			if (EntityManager.HasComponent<Game.Companies.StorageCompany>(entity))
			{
				return entity;
			}

			if (EntityManager.HasBuffer<Renter>(entity))
			{
				DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(entity, true);
				for (int i = 0; i < renters.Length; i++)
				{
					if (EntityManager.HasComponent<Game.Companies.StorageCompany>(renters[i].m_Renter))
					{
						return renters[i].m_Renter;
					}
				}
			}

			return Entity.Null;
		}

		private void StorageCompaniesFromSegment(Entity segment, out Entity start, out Entity end)
		{
			start = end = Entity.Null;
			Entity route = EntityManager.GetComponentData<Owner>(segment).m_Owner;
			if (!EntityManager.HasBuffer<RouteWaypoint>(route))
			{
				return;
			}

			DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(route, true);
			int index = EntityManager.GetComponentData<Game.Routes.Segment>(segment).m_Index;
			int next = index + 1 == waypoints.Length ? 0 : index + 1;
			start = StorageCompanyFromWaypoint(waypoints[index].m_Waypoint);
			end = StorageCompanyFromWaypoint(waypoints[next].m_Waypoint);
		}

		private class CargoLine
		{
			public string Name;
			public float Interval;
			public float StopDuration;
			public readonly List<Entity> Companies = new List<Entity>();
			public readonly List<Entity> Stops = new List<Entity>();
			public readonly List<Entity> Waypoints = new List<Entity>();
			public readonly List<float> SegmentDurations = new List<float>();
			public readonly List<string> Layout = new List<string>();
		}

		// Truck: estimated from the road distance actually driven, with the money weight of StorageTransferSystem
		// (EconomyUtils.GetTransportCost) times ~0.01 per metre plus time at ~14 m/s. Train: boarding cost at the
		// first stop (waiting half an interval minus the stop duration, plus comfort) and the ride time.
		private string CompareHop(StorageTransfer transfer, Section section)
		{
			int amount = math.abs(transfer.m_Amount);
			float weight = 0f;
			Entity resourcePrefab = m_ResourceSystem.GetPrefabs()[transfer.m_Resource];
			if (EntityManager.HasComponent<ResourceData>(resourcePrefab))
			{
				weight = EntityManager.GetComponentData<ResourceData>(resourcePrefab).m_Weight;
			}

			float moneyWeight = EconomyUtils.GetTransportCost(1f, amount, weight, StorageTransferFlags.Car);
			float truckCost = math.dot(section.RoadCosts, new float4(0.01f, 0.01f, moneyWeight, 0.01f));
			StringBuilder sb = new StringBuilder($"{transfer.m_Resource} {amount} w={weight:0.##}: {Describe(section.From)} -> {Describe(section.To)} truck road={section.RoadMetres / 1000f:0.0}km moneyW={moneyWeight:0.###} est={truckCost:0.0} (time={section.RoadCosts.x:0}s money={section.RoadCosts.z:0.0})");

			if (m_Lines == null)
			{
				m_Lines = BuildLines();
			}

			bool any = false;
			foreach (CargoLine line in m_Lines)
			{
				int bestStops = int.MaxValue;
				float bestRide = 0f;
				int bestStart = -1;
				int n = line.Companies.Count;
				for (int a = 0; a < n; a++)
				{
					if (line.Companies[a] != section.From)
					{
						continue;
					}

					float ride = 0f;
					for (int k = 1; k < n; k++)
					{
						ride += line.SegmentDurations[(a + k - 1) % n];
						if (line.Companies[(a + k) % n] == section.To)
						{
							if (k < bestStops)
							{
								bestStops = k;
								bestRide = ride;
								bestStart = a;
							}

							break;
						}
					}
				}

				if (bestStart < 0)
				{
					continue;
				}

				any = true;
				Entity stop = line.Stops[bestStart];
				float comfort = 0f;
				float loading = 1f;
				if (stop != Entity.Null && EntityManager.HasComponent<Game.Routes.TransportStop>(stop))
				{
					Game.Routes.TransportStop ts = EntityManager.GetComponentData<Game.Routes.TransportStop>(stop);
					comfort = ts.m_ComfortFactor;
					loading = ts.m_LoadingFactor;
				}

				float stopDuration = line.StopDuration / math.max(0.25f, loading);
				int avgWaiting = 0;
				if (EntityManager.HasComponent<WaitingPassengers>(line.Waypoints[bestStart]))
				{
					avgWaiting = EntityManager.GetComponentData<WaitingPassengers>(line.Waypoints[bestStart]).m_AverageWaitingTime;
				}

				string flags = "nostop";
				if (stop != Entity.Null && EntityManager.HasComponent<Game.Routes.TransportStop>(stop))
				{
					Game.Routes.TransportStop flagStop = EntityManager.GetComponentData<Game.Routes.TransportStop>(stop);
					flags = flagStop.m_Flags.ToString().Replace(", ", "+") + (flagStop.m_AccessRestriction != Entity.Null ? "+restricted" : "");
				}

				float waitTime = math.max(0f, 5f + math.max(line.Interval * 0.5f, avgWaiting) - stopDuration);
				float comfortCost = 200f * (1f - comfort);
				float trainCost = 0.01f * (waitTime + comfortCost + bestRide);
				sb.Append($" | {line.Name}: {bestStops} stops ride={bestRide:0}s wait={waitTime:0}s avgWait={avgWaiting}s flags={flags} comfort={comfort:0.##} est={trainCost:0.0} through: {ThroughStations(line, bestStart, bestStops, transfer.m_Resource, transfer.m_Amount)}");
			}

			if (!any)
			{
				sb.Append(" | no cargo train line serves both in this direction");
			}

			return sb.ToString();
		}

		private List<CargoLine> BuildLines()
		{
			List<CargoLine> lines = new List<CargoLine>();
			NativeArray<Entity> routes = m_LineQuery.ToEntityArray(Allocator.Temp);
			for (int r = 0; r < routes.Length; r++)
			{
				Entity prefab = EntityManager.GetComponentData<PrefabRef>(routes[r]).m_Prefab;
				if (!EntityManager.HasComponent<TransportLineData>(prefab))
				{
					continue;
				}

				TransportLineData data = EntityManager.GetComponentData<TransportLineData>(prefab);
				if (!data.m_CargoTransport || data.m_TransportType != TransportType.Train)
				{
					continue;
				}

				CargoLine line = new CargoLine
				{
					Name = "train-line#" + routes[r].Index,
					Interval = EntityManager.GetComponentData<TransportLine>(routes[r]).m_VehicleInterval,
					StopDuration = data.m_StopDuration,
				};
				DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(routes[r], true);
				DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(routes[r], true);
				for (int w = 0; w < waypoints.Length; w++)
				{
					Entity waypoint = waypoints[w].m_Waypoint;
					line.Companies.Add(StorageCompanyFromWaypoint(waypoint));
					line.Waypoints.Add(waypoint);
					line.Stops.Add(EntityManager.HasComponent<Connected>(waypoint) ? EntityManager.GetComponentData<Connected>(waypoint).m_Connected : Entity.Null);
					float duration = 0f;
					if (w < segments.Length && EntityManager.HasComponent<RouteInfo>(segments[w].m_Segment))
					{
						duration = EntityManager.GetComponentData<RouteInfo>(segments[w].m_Segment).m_Duration;
					}

					line.Layout.Add(LayoutEntry(w, waypoint, w < segments.Length ? segments[w].m_Segment : Entity.Null));

					line.SegmentDurations.Add(duration);
				}

				lines.Add(line);
			}

			routes.Dispose();
			return lines;
		}

		// Mirrors ResourcePathfindSetup.SetupStorageTransferJob for a cargo station: would this station be offered as
		// a pathfind target for this transfer, and at what cost? Upgrades are ignored. Returns null if not a target.
		private string TargetCost(Entity station, Resource resource, int value)
		{
			if (!EntityManager.HasComponent<PrefabRef>(station))
			{
				return null;
			}

			Entity prefab = EntityManager.GetComponentData<PrefabRef>(station).m_Prefab;
			if (!EntityManager.HasComponent<StorageCompanyData>(prefab) || !EntityManager.HasComponent<StorageLimitData>(prefab))
			{
				return null;
			}

			if (EntityManager.HasComponent<Game.Buildings.TransportStation>(station) &&
				(EntityManager.GetComponentData<Game.Buildings.TransportStation>(station).m_Flags & TransportStationFlags.TransportStopsActive) == 0)
			{
				return null;
			}

			StorageCompanyData data = EntityManager.GetComponentData<StorageCompanyData>(prefab);
			if ((data.m_StoredResources & resource) == Resource.NoResource)
			{
				return null;
			}

			if (EntityManager.HasBuffer<TripNeeded>(station) && EntityManager.GetBuffer<TripNeeded>(station, true).Length > 10)
			{
				return null;
			}

			long stock = EntityManager.HasBuffer<Game.Economy.Resources>(station) ? EconomyUtils.GetResources(resource, EntityManager.GetBuffer<Game.Economy.Resources>(station, true)) : 0;
			int requestCount = 0;
			if (EntityManager.HasBuffer<StorageTransferRequest>(station))
			{
				DynamicBuffer<StorageTransferRequest> requests = EntityManager.GetBuffer<StorageTransferRequest>(station, true);
				requestCount = requests.Length;
				for (int i = 0; i < requests.Length; i++)
				{
					if (requests[i].m_Resource == resource)
					{
						stock += (requests[i].m_Flags & StorageTransferFlags.Incoming) != 0 ? requests[i].m_Amount : -requests[i].m_Amount;
					}
				}
			}

			long perResource = EntityManager.GetComponentData<StorageLimitData>(prefab).m_Limit / math.max(1, EconomyUtils.CountResources(data.m_StoredResources));
			long fit = value <= 0 ? -math.min(-value, stock) : math.min(value, perResource - stock);
			if (math.abs(fit) < 4000 || value == 0 || fit / value == 0)
			{
				return null;
			}

			float reward = 1000f * math.abs(fit / value);
			TradeCost tradeCost = EntityManager.HasBuffer<TradeCost>(station) ? EconomyUtils.GetTradeCost(resource, EntityManager.GetBuffer<TradeCost>(station, true)) : default;
			float cost = 0.01f * value * math.max(0.1f, value > 0 ? tradeCost.m_SellCost : -tradeCost.m_BuyCost) + requestCount * 0.0001f;
			int maxTransports = EntityManager.HasComponent<TransportCompanyData>(prefab) ? EntityManager.GetComponentData<TransportCompanyData>(prefab).m_MaxTransports : 0;
			if (EntityManager.HasBuffer<OwnedVehicle>(station))
			{
				int owned = EntityManager.GetBuffer<OwnedVehicle>(station, true).Length;
				if (owned >= maxTransports)
				{
					cost += 5000f * owned / math.max(1, maxTransports);
				}
			}

			return (cost - reward).ToString("0");
		}

		// Stations the best train alternative would have passed through (strictly between From and To).
		private string ThroughStations(CargoLine line, int start, int stops, Resource resource, int value)
		{
			StringBuilder sb = new StringBuilder();
			int n = line.Companies.Count;
			for (int k = 1; k < stops; k++)
			{
				Entity company = line.Companies[(start + k) % n];
				string target = company != Entity.Null && IsStation(company) ? TargetCost(company, resource, value) : null;
				sb.Append(company != Entity.Null ? Kind(company) + "#" + company.Index : "none").Append(target != null ? "=T" + target : "=no").Append(' ');
			}

			return sb.ToString();
		}

		private void CountRideThrough(DynamicBuffer<PathElement> path, StorageTransfer transfer)
		{
			for (int i = 1; i < path.Length; i++)
			{
				if (!EntityManager.HasComponent<Game.Routes.Segment>(path[i].m_Target) || !EntityManager.HasComponent<Game.Routes.Segment>(path[i - 1].m_Target))
				{
					continue;
				}

				StorageCompaniesFromSegment(path[i].m_Target, out Entity through, out _);
				if (through == Entity.Null || !IsStation(through))
				{
					continue;
				}

				if (TargetCost(through, transfer.m_Resource, transfer.m_Amount) != null)
				{
					m_RideThroughTargets++;
				}
				else
				{
					m_RideThroughOther++;
				}
			}
		}

		private string LayoutEntry(int w, Entity waypoint, Entity segment)
		{
			Entity company = StorageCompanyFromWaypoint(waypoint);
			string at = company != Entity.Null ? Describe(company) : "none";
			if (EntityManager.HasComponent<Connected>(waypoint))
			{
				Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
				if (EntityManager.HasComponent<Game.Routes.TransportStop>(stop))
				{
					at += " stop=" + EntityManager.GetComponentData<Game.Routes.TransportStop>(stop).m_Flags.ToString().Replace(", ", "+");
				}
				else
				{
					at += " conn=" + (EntityManager.HasComponent<PrefabRef>(stop) ? m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(stop).m_Prefab) : "?");
				}
			}

			string seg = "noseg";
			if (segment != Entity.Null && EntityManager.HasComponent<RouteInfo>(segment))
			{
				RouteInfo info = EntityManager.GetComponentData<RouteInfo>(segment);
				int index = EntityManager.GetComponentData<Game.Routes.Segment>(segment).m_Index;
				seg = $"seg i{index} {info.m_Distance / 1000f:0.0}km {info.m_Duration:0}s {info.m_Flags}";
			}

			return $"{w}:{at} -> {seg}";
		}

		private bool IsStation(Entity entity)
		{
			return EntityManager.HasComponent<Game.Buildings.CargoTransportStation>(entity) && !EntityManager.HasComponent<Game.Objects.OutsideConnection>(entity);
		}

		private string Kind(Entity entity)
		{
			if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(entity))
			{
				return "outside";
			}

			if (EntityManager.HasComponent<Game.Buildings.CargoTransportStation>(entity))
			{
				return "station";
			}

			return EntityManager.HasComponent<Game.Companies.StorageCompany>(entity) ? "warehouse" : "company";
		}

		private string Describe(Entity entity)
		{
			string name = EntityManager.HasComponent<PrefabRef>(entity) ? m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab) : "?";
			return Kind(entity) + ":" + name + "#" + entity.Index;
		}

		private void Report()
		{
			if (m_Paths == 0)
			{
				return;
			}

			List<KeyValuePair<string, int>> patterns = new List<KeyValuePair<string, int>>(m_PatternCounts);
			patterns.Sort((a, b) => b.Value.CompareTo(a.Value));
			StringBuilder sb = new StringBuilder($"[Diag] RouteCapture paths={m_Paths} withTruckBetweenStations={m_PathsWithRoadBetweenStations} rideThroughStations: target={m_RideThroughTargets} notTarget={m_RideThroughOther} topPatterns: ");
			for (int i = 0; i < math.min(patterns.Count, 12); i++)
			{
				sb.Append(patterns[i].Key).Append(" =").Append(patterns[i].Value).Append(" | ");
			}

			Mod.log.Info(sb.ToString());
			foreach (string sample in m_Samples)
			{
				Mod.log.Info("[Diag] RouteSample " + sample);
			}

			foreach (string hop in m_Hops)
			{
				Mod.log.Info("[Diag] RouteHop " + hop);
			}

			if (m_LayoutReports < 2)
			{
				m_LayoutReports++;
				foreach (CargoLine line in m_Lines ?? BuildLines())
				{
					Mod.log.Info("[Diag] LineLayout " + line.Name + " interval=" + line.Interval.ToString("0") + "s: " + string.Join(" | ", line.Layout));
				}
			}

			m_Hops.Clear();
			m_Lines = null;

			m_PatternCounts.Clear();
			m_Samples.Clear();
			m_Paths = 0;
			m_PathsWithRoadBetweenStations = 0;
			m_RideThroughTargets = 0;
			m_RideThroughOther = 0;
			if (m_Seen.Count > 20000)
			{
				m_Seen.Clear();
			}
		}
	}
}

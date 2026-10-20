using System.Collections.Generic;
using Game;
using Game.Common;
using Game.Companies;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	// Sends cargo by train where the vanilla route planner sends a truck between two cargo stations that a cargo
	// train line connects. The planner often cannot reach the train at those stations (it is not a cost choice:
	// the truck hop costs far more than the ride), so the finished route of a storage transfer is edited before
	// StorageTransferSystem turns it into transfer requests: the truck section between the two stations is replaced
	// by the line's segments, exactly as the planner writes a train ride. HandleCargoPath then creates one Transport
	// request per passed station instead of a truck trip.
	public partial class RailFreightRerouteSystem : GameSystemBase
	{
		private const uint kReportFrames = 4096;

		private struct Section
		{
			public Entity From;
			public Entity To;
			public bool Car;
			public bool Ride;
			public int First; // index of the element that closed the previous section (-1 at the origin)
			public int Last;  // index of the element that closes this section
		}

		private struct Ride
		{
			public Entity Route;
			public int Start;
			public int Stops;
			public float Duration;
		}

		private class CargoLine
		{
			public Entity Route;
			public Entity[] Companies;
			public Entity[] Segments;
			public float[] Durations;
			public bool[] Usable;
		}

		private EntityQuery m_StorageQuery;
		private EntityQuery m_LineQuery;
		private SimulationSystem m_SimulationSystem;

		private readonly HashSet<Entity> m_Done = new HashSet<Entity>();
		private List<CargoLine> m_Lines;
		private uint m_LinesFrame;
		private uint m_LastReport;
		private int m_Paths;
		private int m_Rerouted;
		private int m_NoLine;
		private int m_Mixed;
		private int m_Stale;
		private bool m_LoggedFirst;
		private readonly List<string> m_Samples = new List<string>();
		private PrefabSystem m_PrefabSystem;

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();
			m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
			m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
			m_StorageQuery = GetEntityQuery(
				ComponentType.ReadOnly<StorageTransfer>(),
				ComponentType.ReadOnly<PathInformation>(),
				ComponentType.ReadWrite<PathElement>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
			m_LineQuery = GetEntityQuery(
				ComponentType.ReadOnly<TransportLine>(),
				ComponentType.ReadOnly<RouteWaypoint>(),
				ComponentType.ReadOnly<RouteSegment>(),
				ComponentType.ReadOnly<PrefabRef>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
		}

		// Same interval as StorageTransferSystem and no own offset: the update system then gives this system (registered
		// UpdateBefore it) the same frame, so every finished route is seen once, just before it is consumed.
		public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

		[Preserve]
		protected override void OnUpdate()
		{
			if (Mod.Settings == null || !Mod.Settings.BulkFreightOptimisation)
			{
				m_Done.Clear();
				return;
			}

			uint frame = m_SimulationSystem.frameIndex;
			NativeArray<Entity> entities = m_StorageQuery.ToEntityArray(Allocator.Temp);
			for (int i = 0; i < entities.Length; i++)
			{
				PathInformation info = EntityManager.GetComponentData<PathInformation>(entities[i]);
				if ((info.m_State & PathFlags.Pending) != 0)
				{
					m_Done.Remove(entities[i]);
					continue;
				}

				if (!m_Done.Add(entities[i]))
				{
					continue;
				}

				if (m_Lines == null || frame - m_LinesFrame >= 1024)
				{
					m_Lines = BuildLines();
					m_LinesFrame = frame;
				}

				try
				{
					Reroute(entities[i], info);
				}
				catch (System.Exception ex)
				{
					Mod.log.Warn("RailFreightRerouteSystem: reroute failed: " + ex.Message);
				}
			}

			entities.Dispose();

			if (frame - m_LastReport >= kReportFrames)
			{
				m_LastReport = frame;
				// Diagnostics only: a summary of what was moved and samples of the truck hops that were kept.
				if (Mod.EnableDiagnostics && m_Paths > 0)
				{
					Mod.log.Info($"[Rail] routes={m_Paths} truckHopsMovedToRail={m_Rerouted} truckHopsWithoutLine={m_NoLine} truckHopsMixedWithRide={m_Mixed} staleLineSkipped={m_Stale}");
					foreach (string sample in m_Samples)
					{
						Mod.log.Info("[Rail] kept truck hop " + sample);
					}

				}

				m_Paths = m_Rerouted = m_NoLine = m_Mixed = m_Stale = 0;
				m_Samples.Clear();
				if (m_Done.Count > 20000)
				{
					m_Done.Clear();
				}
			}
		}

		private void Reroute(Entity storage, PathInformation info)
		{
			DynamicBuffer<PathElement> path = EntityManager.GetBuffer<PathElement>(storage);
			if (path.Length == 0 || info.m_Origin == Entity.Null || info.m_Destination == Entity.Null)
			{
				return;
			}

			m_Paths++;
			List<Section> sections = Split(info, path);
			List<PathElement> rebuilt = null;
			int copied = 0; // elements [0, copied) of the old path are already in rebuilt (or dropped)
			for (int s = 0; s < sections.Count; s++)
			{
				Section section = sections[s];
				if (!section.Car || !IsStation(section.From) || !IsStation(section.To))
				{
					continue;
				}

				// A section that drives and also rides part of a line ("mixed") still becomes a truck trip in
				// HandleCargoPath (Car flag), so it is replaced the same way.
				if (!FindRide(section.From, section.To, out Ride ride, out CargoLine line))
				{
					if (section.Ride)
					{
						m_Mixed++;
					}
					else
					{
						m_NoLine++;
					}

					Sample(section.Ride ? "mixed" : "noLine", storage, section);
					continue;
				}

				// The line cache can be up to ~17 s old: if the player changed or removed the line since, keep
				// the truck and rebuild the cache for the next route.
				if (!RideIsCurrent(line, ride, section.From, section.To))
				{
					m_Lines = null;
					m_Stale++;
					continue;
				}

				if (rebuilt == null)
				{
					rebuilt = new List<PathElement>(path.Length);
				}

				// Keep everything up to and including the element that brought the route into From. At the
				// origin the section starts at the first element, which is dropped with the rest of the truck hop.
				for (int i = copied; i <= section.First; i++)
				{
					rebuilt.Add(path[i]);
				}

				int n = line.Companies.Length;
				for (int k = 0; k < ride.Stops; k++)
				{
					rebuilt.Add(new PathElement(line.Segments[(ride.Start + k) % n], new float2(0f, 1f)));
				}

				// The element that closes the truck section (To's lane or the next ride's first segment) stays.
				copied = section.Last;
				m_Rerouted++;
				if (!m_LoggedFirst)
				{
					m_LoggedFirst = true;
					Mod.log.Info("Bulk freight optimisation: first truck hop between stations moved to a cargo line this session.");
				}
			}

			if (rebuilt == null)
			{
				return;
			}

			for (int i = copied; i < path.Length; i++)
			{
				rebuilt.Add(path[i]);
			}

			path.Clear();
			for (int i = 0; i < rebuilt.Count; i++)
			{
				path.Add(rebuilt[i]);
			}
		}

		// Checks the cached ride against the live line: every segment still exists, still belongs to the same route,
		// and the ride still starts at From and ends at To.
		private bool RideIsCurrent(CargoLine line, Ride ride, Entity from, Entity to)
		{
			if (!EntityManager.Exists(line.Route) || EntityManager.HasComponent<Deleted>(line.Route))
			{
				return false;
			}

			int n = line.Companies.Length;
			for (int k = 0; k < ride.Stops; k++)
			{
				Entity segment = line.Segments[(ride.Start + k) % n];
				if (segment == Entity.Null || !EntityManager.Exists(segment) || EntityManager.HasComponent<Deleted>(segment) ||
					!EntityManager.HasComponent<Game.Routes.Segment>(segment) || !EntityManager.HasComponent<Owner>(segment) ||
					EntityManager.GetComponentData<Owner>(segment).m_Owner != line.Route)
				{
					return false;
				}
			}

			StorageCompaniesFromSegment(line.Segments[ride.Start % n], out Entity start, out _);
			StorageCompaniesFromSegment(line.Segments[(ride.Start + ride.Stops - 1) % n], out _, out Entity end);
			return start == from && end == to;
		}

		private void Sample(string reason, Entity storage, Section section)
		{
			if (!Mod.EnableDiagnostics)
			{
				return;
			}

			if (m_Samples.Count >= 12)
			{
				return;
			}

			StorageTransfer transfer = EntityManager.GetComponentData<StorageTransfer>(storage);
			m_Samples.Add($"{reason} {transfer.m_Resource} {math.abs(transfer.m_Amount)}: {Describe(section.From)} -> {Describe(section.To)} lines: {LinesAt(section.From)} / {LinesAt(section.To)}");
		}

		private string Describe(Entity entity)
		{
			string name = EntityManager.HasComponent<PrefabRef>(entity) ? m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab) : "?";
			return name + "#" + entity.Index;
		}

		// Which cargo lines stop at this company, for the kept-hop samples.
		private string LinesAt(Entity company)
		{
			List<string> names = new List<string>();
			foreach (CargoLine line in m_Lines)
			{
				if (System.Array.IndexOf(line.Companies, company) >= 0)
				{
					Entity prefab = EntityManager.GetComponentData<PrefabRef>(line.Route).m_Prefab;
					names.Add(EntityManager.GetComponentData<TransportLineData>(prefab).m_TransportType.ToString().ToLowerInvariant() + "#" + line.Route.Index);
				}
			}

			return names.Count == 0 ? "none" : string.Join(",", names);
		}

		// Shortest ride (by time) from one station to the next occurrence of the other on any cargo train line.
		private bool FindRide(Entity from, Entity to, out Ride best, out CargoLine bestLine)
		{
			best = default;
			bestLine = null;
			float bestDuration = float.MaxValue;
			foreach (CargoLine line in m_Lines)
			{
				int n = line.Companies.Length;
				for (int a = 0; a < n; a++)
				{
					if (line.Companies[a] != from)
					{
						continue;
					}

					float duration = 0f;
					for (int k = 1; k < n; k++)
					{
						int segment = (a + k - 1) % n;
						if (!line.Usable[segment])
						{
							break;
						}

						duration += line.Durations[segment];
						if (line.Companies[(a + k) % n] == to)
						{
							if (duration < bestDuration)
							{
								bestDuration = duration;
								best = new Ride { Route = line.Route, Start = a, Stops = k, Duration = duration };
								bestLine = line;
							}

							break;
						}
					}
				}
			}

			return bestLine != null;
		}

		// Mirrors StorageTransferSystem.HandleCargoPath: a section ends at every storage company the route enters,
		// via its pedestrian/cargo lane or a line stop.
		private List<Section> Split(PathInformation info, DynamicBuffer<PathElement> path)
		{
			List<Section> sections = new List<Section>();
			Entity current = info.m_Origin;
			bool car = false;
			bool ride = false;
			int first = -1;

			for (int i = 0; i < path.Length; i++)
			{
				Entity target = path[i].m_Target;
				if (EntityManager.HasComponent<Game.Net.CarLane>(target))
				{
					car = true;
				}
				else if (EntityManager.HasComponent<Game.Net.PedestrianLane>(target))
				{
					Entity company = StorageCompanyFromLane(target);
					if (company != Entity.Null && company != current)
					{
						sections.Add(new Section { From = current, To = company, Car = car, Ride = ride, First = first, Last = i });
						current = company;
						car = ride = false;
						first = i;
					}
				}
				else if (EntityManager.HasComponent<Game.Net.ConnectionLane>(target))
				{
					if ((EntityManager.GetComponentData<Game.Net.ConnectionLane>(target).m_Flags & ConnectionLaneFlags.Road) != 0)
					{
						car = true;
					}
				}
				else if (EntityManager.HasComponent<Game.Routes.Segment>(target))
				{
					StorageCompaniesFromSegment(target, out Entity start, out Entity end);
					if (start != Entity.Null && start != current)
					{
						sections.Add(new Section { From = current, To = start, Car = car, Ride = ride, First = first, Last = i });
						current = start;
						car = ride = false;
						first = i;
					}

					ride = true;
					if (end != Entity.Null && end != current)
					{
						sections.Add(new Section { From = current, To = end, Car = car, Ride = ride, First = first, Last = i });
						current = end;
						car = ride = false;
						first = i;
					}
				}
			}

			return sections;
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
				if (!data.m_CargoTransport)
				{
					continue;
				}

				DynamicBuffer<RouteWaypoint> waypoints = EntityManager.GetBuffer<RouteWaypoint>(routes[r], true);
				DynamicBuffer<RouteSegment> segments = EntityManager.GetBuffer<RouteSegment>(routes[r], true);
				int n = waypoints.Length;
				if (n < 2 || segments.Length != n)
				{
					continue;
				}

				CargoLine line = new CargoLine
				{
					Route = routes[r],
					Companies = new Entity[n],
					Segments = new Entity[n],
					Durations = new float[n],
					Usable = new bool[n],
				};

				for (int w = 0; w < n; w++)
				{
					line.Companies[w] = StopCompany(waypoints[w].m_Waypoint);
				}

				for (int s = 0; s < n; s++)
				{
					Entity segment = segments[s].m_Segment;
					int index = EntityManager.GetComponentData<Game.Routes.Segment>(segment).m_Index;
					if (index < 0 || index >= n)
					{
						continue;
					}

					line.Segments[index] = segment;
					if (EntityManager.HasComponent<RouteInfo>(segment))
					{
						RouteInfo routeInfo = EntityManager.GetComponentData<RouteInfo>(segment);
						line.Durations[index] = routeInfo.m_Duration;
						line.Usable[index] = routeInfo.m_Distance > 0f && routeInfo.m_Duration > 0f;
					}
				}

				lines.Add(line);
			}

			routes.Dispose();
			return lines;
		}

		// The storage company served at a waypoint, only if its stop is active and accepts cargo.
		private Entity StopCompany(Entity waypoint)
		{
			if (!EntityManager.HasComponent<Connected>(waypoint))
			{
				return Entity.Null;
			}

			Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;
			if (EntityManager.HasComponent<Game.Routes.TransportStop>(stop) &&
				(EntityManager.GetComponentData<Game.Routes.TransportStop>(stop).m_Flags & StopFlags.Active) == 0)
			{
				return Entity.Null;
			}

			return StorageCompanyFromWaypoint(waypoint);
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

			if (EntityManager.HasBuffer<Game.Buildings.Renter>(entity))
			{
				DynamicBuffer<Game.Buildings.Renter> renters = EntityManager.GetBuffer<Game.Buildings.Renter>(entity, true);
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

		private bool IsStation(Entity entity)
		{
			return EntityManager.HasComponent<Game.Buildings.CargoTransportStation>(entity) && !EntityManager.HasComponent<Game.Objects.OutsideConnection>(entity);
		}
	}
}

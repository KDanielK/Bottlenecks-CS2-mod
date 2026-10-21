using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;

namespace Bottlenecks.Systems
{
	// Opt-in: periodically logs the state of every cargo station, to find out why companies import
	// instead of buying from stocked stations and why stations move goods by truck.
	public partial class CargoDiagnosticsSystem : GameSystemBase
	{
		private EntityQuery m_CargoStationQuery;
		private EntityQuery m_ResourceBuyerQuery;

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();
			m_CargoStationQuery = GetEntityQuery(
				ComponentType.ReadOnly<Game.Buildings.CargoTransportStation>(),
				ComponentType.ReadOnly<Game.Companies.StorageCompany>(),
				ComponentType.ReadOnly<PrefabRef>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Destroyed>(),
				ComponentType.Exclude<Temp>());
			m_ResourceBuyerQuery = GetEntityQuery(
				ComponentType.ReadOnly<ResourceBuyer>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
		}

		public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

		[Preserve]
		protected override void OnUpdate()
		{
			if (!Mod.EnableDiagnostics)
			{
				return;
			}

			PrefabSystem prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
			LogCargoStations(prefabSystem);
			LogTransferDestinations();
			LogQueuedTripTargets();
			LogCargoLineSpeeds(prefabSystem);
			LogResourceBuyers();
		}

		// The pathfinder's A* heuristic for cargo uses 2 x 0.01 / maxCargoTransportSpeed per metre (the fastest
		// cargo line in the city). If a line is slower than half of that, the heuristic overestimates its cost.
		private void LogCargoLineSpeeds(PrefabSystem prefabSystem)
		{
			World.GetOrCreateSystemManaged<Game.Simulation.TransportLineSystem>().GetMaxTransportSpeed(out float maxPassenger, out float maxCargo);
			Mod.log.Info($"[Diag] CargoLines maxCargoTransportSpeed={maxCargo:0.0} m/s (heuristic {2f * 0.01f / math.max(0.01f, maxCargo):0.000000}/m), maxPassengerTransportSpeed={maxPassenger:0.0} m/s");

			EntityQuery lines = GetEntityQuery(ComponentType.ReadOnly<Game.Routes.TransportLine>(), ComponentType.ReadOnly<PrefabRef>(), ComponentType.Exclude<Deleted>(), ComponentType.Exclude<Temp>());
			NativeArray<Entity> routes = lines.ToEntityArray(Allocator.Temp);
			try
			{
				for (int i = 0; i < routes.Length; i++)
				{
					Entity prefab = EntityManager.GetComponentData<PrefabRef>(routes[i]).m_Prefab;
					if (!EntityManager.HasComponent<TransportLineData>(prefab) || !EntityManager.GetComponentData<TransportLineData>(prefab).m_CargoTransport)
					{
						continue;
					}

					TransportLineData lineData = EntityManager.GetComponentData<TransportLineData>(prefab);
					float distance = 0f;
					float duration = 0f;
					float maxSegmentSpeed = 0f;
					int segmentCount = 0;
					if (EntityManager.HasBuffer<Game.Routes.RouteSegment>(routes[i]))
					{
						DynamicBuffer<Game.Routes.RouteSegment> segments = EntityManager.GetBuffer<Game.Routes.RouteSegment>(routes[i], true);
						for (int s = 0; s < segments.Length; s++)
						{
							if (!EntityManager.HasComponent<Game.Routes.RouteInfo>(segments[s].m_Segment))
							{
								continue;
							}

							Game.Routes.RouteInfo info = EntityManager.GetComponentData<Game.Routes.RouteInfo>(segments[s].m_Segment);
							distance += info.m_Distance;
							duration += info.m_Duration;
							maxSegmentSpeed = math.max(maxSegmentSpeed, math.max(1f, info.m_Distance) / math.max(1f, info.m_Duration));
							segmentCount++;
						}
					}

					float avgSpeed = duration > 0f ? distance / duration : 0f;
					Game.Routes.TransportLine line = EntityManager.GetComponentData<Game.Routes.TransportLine>(routes[i]);
					Mod.log.Info($"[Diag] CargoLine idx={routes[i].Index} {prefabSystem.GetPrefabName(prefab)} ({lineData.m_TransportType}) segments={segmentCount} length={distance / 1000f:0.0}km duration={duration:0}s avgSpeed={avgSpeed:0.0}m/s maxSegmentSpeed={maxSegmentSpeed:0.0}m/s realCost={(avgSpeed > 0f ? 0.01f / avgSpeed : 0f):0.000000}/m interval={line.m_VehicleInterval:0}s");
				}
			}
			finally
			{
				routes.Dispose();
			}
		}

		// Where do the stations' outgoing transfer requests go, by mode? Groups requests by target kind and
		// straight-line distance, to see whether short station-to-station hops really go by truck.
		private void LogTransferDestinations()
		{
			NativeArray<Entity> stations = m_CargoStationQuery.ToEntityArray(Allocator.Temp);
			Dictionary<string, long[]> groups = new Dictionary<string, long[]>();
			try
			{
				for (int i = 0; i < stations.Length; i++)
				{
					if (!EntityManager.HasBuffer<StorageTransferRequest>(stations[i]))
					{
						continue;
					}

					float3 from = GetPosition(stations[i], out bool hasFrom);
					DynamicBuffer<StorageTransferRequest> requests = EntityManager.GetBuffer<StorageTransferRequest>(stations[i], true);
					for (int r = 0; r < requests.Length; r++)
					{
						StorageTransferRequest request = requests[r];
						if ((request.m_Flags & StorageTransferFlags.Incoming) != 0)
						{
							continue;
						}

						string mode = (request.m_Flags & StorageTransferFlags.Car) != 0 ? "truck"
							: ((request.m_Flags & StorageTransferFlags.Transport) != 0 ? "rail" : "other");
						float3 to = GetPosition(request.m_Target, out bool hasTo);
						string distance = hasFrom && hasTo ? DistanceBucket(math.distance(from.xz, to.xz)) : "?";
						string key = mode + " " + PrefabName(stations[i]) + " -> " + TargetKind(request.m_Target) + ":" + PrefabName(request.m_Target) + " " + distance;
						if (!groups.TryGetValue(key, out long[] sums))
						{
							sums = new long[2];
							groups[key] = sums;
						}

						sums[0]++;
						sums[1] += math.abs(request.m_Amount);
					}
				}
			}
			finally
			{
				stations.Dispose();
			}

			List<KeyValuePair<string, long[]>> sorted = new List<KeyValuePair<string, long[]>>(groups);
			sorted.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));
			StringBuilder sb = new StringBuilder("[Diag] TransferDest outgoing requests: ");
			foreach (KeyValuePair<string, long[]> group in sorted)
			{
				sb.Append(group.Key).Append('=').Append(group.Value[0]).Append(" (").Append((group.Value[1] / 1e6f).ToString("0.0")).Append("M); ");
			}

			Mod.log.Info(sb.ToString());
		}

		// Where do the trips already queued at the stations (TripNeeded, served in order by the station's trucks) go?
		// Station-to-station storage transfers in this queue were booked as truck legs before; with bulk freight
		// optimisation on, new ones should only appear where no cargo line connects the two.
		private void LogQueuedTripTargets()
		{
			NativeArray<Entity> stations = m_CargoStationQuery.ToEntityArray(Allocator.Temp);
			Dictionary<string, int> groups = new Dictionary<string, int>();
			int total = 0;
			try
			{
				for (int i = 0; i < stations.Length; i++)
				{
					if (!EntityManager.HasBuffer<TripNeeded>(stations[i]))
					{
						continue;
					}

					DynamicBuffer<TripNeeded> trips = EntityManager.GetBuffer<TripNeeded>(stations[i], true);
					for (int t = 0; t < trips.Length; t++)
					{
						string key = trips[t].m_Purpose + " -> " + TargetKind(trips[t].m_TargetAgent);
						groups.TryGetValue(key, out int count);
						groups[key] = count + 1;
						total++;
					}
				}
			}
			finally
			{
				stations.Dispose();
			}

			List<KeyValuePair<string, int>> sorted = new List<KeyValuePair<string, int>>(groups);
			sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
			StringBuilder sb = new StringBuilder($"[Diag] QueuedTrips total={total}: ");
			foreach (KeyValuePair<string, int> group in sorted)
			{
				sb.Append(group.Key).Append('=').Append(group.Value).Append("; ");
			}

			Mod.log.Info(sb.ToString());
		}

		private string TargetKind(Entity target)
		{
			if (target == Entity.Null || !EntityManager.Exists(target))
			{
				return "missing";
			}

			if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(target))
			{
				return "outside";
			}

			if (EntityManager.HasComponent<Game.Buildings.CargoTransportStation>(target))
			{
				return "station";
			}

			if (EntityManager.HasComponent<Game.Companies.StorageCompany>(target))
			{
				return "warehouse";
			}

			return EntityManager.HasComponent<PropertyRenter>(target) ? "company" : "other";
		}

		private string PrefabName(Entity entity)
		{
			if (entity == Entity.Null || !EntityManager.Exists(entity) || !EntityManager.HasComponent<PrefabRef>(entity))
			{
				return "?";
			}

			return World.GetOrCreateSystemManaged<PrefabSystem>().GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
		}

		// Companies have no transform themselves; use their building.
		private float3 GetPosition(Entity entity, out bool found)
		{
			if (entity != Entity.Null && EntityManager.Exists(entity) && EntityManager.HasComponent<PropertyRenter>(entity))
			{
				entity = EntityManager.GetComponentData<PropertyRenter>(entity).m_Property;
			}

			found = entity != Entity.Null && EntityManager.Exists(entity) && EntityManager.HasComponent<Game.Objects.Transform>(entity);
			return found ? EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position : default;
		}

		private static string DistanceBucket(float metres)
		{
			return metres < 1000f ? "<1km" : (metres < 3000f ? "1-3km" : (metres < 8000f ? "3-8km" : ">8km"));
		}

		private void LogCargoStations(PrefabSystem prefabSystem)
		{
			NativeArray<Entity> entities = m_CargoStationQuery.ToEntityArray(Allocator.Temp);
			NativeArray<PrefabRef> prefabRefs = m_CargoStationQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
			try
			{
				for (int i = 0; i < entities.Length; i++)
				{
					try
					{
						LogOneCargoStation(prefabSystem, entities[i], prefabRefs[i]);
					}
					catch (Exception ex)
					{
						Mod.log.Info("[Diag] CargoStation entity " + entities[i].Index + " failed to report: " + ex.Message);
					}
				}
			}
			finally
			{
				entities.Dispose();
				prefabRefs.Dispose();
			}
		}

		private void LogOneCargoStation(PrefabSystem prefabSystem, Entity entity, PrefabRef prefabRef)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("[Diag] CargoStation idx=").Append(entity.Index);
			sb.Append(" prefab=").Append(prefabSystem.GetPrefabName(prefabRef.m_Prefab));
			sb.Append(" outside=").Append(EntityManager.HasComponent<Game.Objects.OutsideConnection>(entity));

			if (EntityManager.HasBuffer<TripNeeded>(entity))
			{
				DynamicBuffer<TripNeeded> tripNeeded = EntityManager.GetBuffer<TripNeeded>(entity, true);
				Dictionary<Purpose, int> purposeCounts = new Dictionary<Purpose, int>();
				for (int t = 0; t < tripNeeded.Length; t++)
				{
					Purpose purpose = tripNeeded[t].m_Purpose;
					purposeCounts.TryGetValue(purpose, out int count);
					purposeCounts[purpose] = count + 1;
				}

				sb.Append(" tripNeeded=").Append(tripNeeded.Length);
				sb.Append(" purposes=");
				foreach (KeyValuePair<Purpose, int> kv in purposeCounts)
				{
					sb.Append(kv.Key.ToString()).Append(':').Append(kv.Value).Append(',');
				}

				// Company buyers skip a cargo station as a seller at this queue length.
				if (tripNeeded.Length >= ResourcePathfindSetup.kCargoStationMaxTripNeededQueue)
				{
					sb.Append(" SKIPPED_AS_SELLER");
				}
			}
			else
			{
				sb.Append(" tripNeeded=0");
			}

			int ownedCount = EntityManager.HasBuffer<OwnedVehicle>(entity) ? EntityManager.GetBuffer<OwnedVehicle>(entity, true).Length : 0;
			sb.Append(" ownedVehicles=").Append(ownedCount);
			if (EntityManager.HasComponent<TransportCompanyData>(prefabRef.m_Prefab))
			{
				sb.Append(" maxTransports=").Append(EntityManager.GetComponentData<TransportCompanyData>(prefabRef.m_Prefab).m_MaxTransports);
			}

			if (EntityManager.HasBuffer<StorageTransferRequest>(entity))
			{
				DynamicBuffer<StorageTransferRequest> transferRequests = EntityManager.GetBuffer<StorageTransferRequest>(entity, true);
				int car = 0;
				int transport = 0;
				int track = 0;
				int incoming = 0;
				long carAmt = 0;
				long transportAmt = 0;
				long trackAmt = 0;
				long incomingAmt = 0;
				for (int r = 0; r < transferRequests.Length; r++)
				{
					StorageTransferRequest request = transferRequests[r];
					if ((request.m_Flags & StorageTransferFlags.Car) != 0)
					{
						car++;
						carAmt += request.m_Amount;
					}

					if ((request.m_Flags & StorageTransferFlags.Transport) != 0)
					{
						transport++;
						transportAmt += request.m_Amount;
					}

					if ((request.m_Flags & StorageTransferFlags.Track) != 0)
					{
						track++;
						trackAmt += request.m_Amount;
					}

					if ((request.m_Flags & StorageTransferFlags.Incoming) != 0)
					{
						incoming++;
						incomingAmt += request.m_Amount;
					}
				}

				sb.Append(" transferReq=").Append(transferRequests.Length);
				sb.Append(" car=").Append(car).Append('/').Append(carAmt);
				sb.Append(" transport=").Append(transport).Append('/').Append(transportAmt);
				sb.Append(" track=").Append(track).Append('/').Append(trackAmt);
				sb.Append(" incoming=").Append(incoming).Append('/').Append(incomingAmt);
			}
			else
			{
				sb.Append(" transferReq=0");
			}

			long perResourceLimit = 0;
			if (EntityManager.HasComponent<StorageLimitData>(prefabRef.m_Prefab))
			{
				int countResources = 1;
				if (EntityManager.HasComponent<StorageCompanyData>(prefabRef.m_Prefab))
				{
					countResources = math.max(1, EconomyUtils.CountResources(EntityManager.GetComponentData<StorageCompanyData>(prefabRef.m_Prefab).m_StoredResources));
				}

				perResourceLimit = EntityManager.GetComponentData<StorageLimitData>(prefabRef.m_Prefab).m_Limit / countResources;
				sb.Append(" perResourceLimit=").Append(perResourceLimit);
			}

			if (EntityManager.HasBuffer<ConnectedRoute>(entity))
			{
				sb.Append(" connectedRoutes=").Append(EntityManager.GetBuffer<ConnectedRoute>(entity, true).Length);
			}

			Mod.log.Info(sb.ToString());

			LogStoredResources(entity, perResourceLimit);
		}

		private void LogStoredResources(Entity entity, long perResourceLimit)
		{
			if (!EntityManager.HasBuffer<Game.Economy.Resources>(entity))
			{
				return;
			}

			DynamicBuffer<Game.Economy.Resources> resources = EntityManager.GetBuffer<Game.Economy.Resources>(entity, true);
			List<KeyValuePair<Resource, int>> list = new List<KeyValuePair<Resource, int>>();
			for (int i = 0; i < resources.Length; i++)
			{
				if (resources[i].m_Amount > 0)
				{
					list.Add(new KeyValuePair<Resource, int>(resources[i].m_Resource, resources[i].m_Amount));
				}
			}

			if (list.Count == 0)
			{
				return;
			}

			list.Sort((a, b) => b.Value.CompareTo(a.Value));
			int top = math.min(list.Count, 8);

			bool hasTradeCosts = EntityManager.HasBuffer<TradeCost>(entity);
			DynamicBuffer<TradeCost> tradeCosts = hasTradeCosts ? EntityManager.GetBuffer<TradeCost>(entity, true) : default;

			for (int i = 0; i < top; i++)
			{
				Resource resource = list[i].Key;
				float buyCost = 0f;
				float sellCost = 0f;
				if (hasTradeCosts)
				{
					TradeCost cost = EconomyUtils.GetTradeCost(resource, tradeCosts);
					buyCost = cost.m_BuyCost;
					sellCost = cost.m_SellCost;
				}

				Mod.log.Info("[Diag] CargoStation idx=" + entity.Index +
					" resource=" + resource.ToString() +
					" amount=" + list[i].Value +
					" perResourceLimit=" + perResourceLimit +
					" buyCost=" + buyCost +
					" sellCost=" + sellCost);
			}
		}

		private void LogResourceBuyers()
		{
			NativeArray<ResourceBuyer> buyers = m_ResourceBuyerQuery.ToComponentDataArray<ResourceBuyer>(Allocator.Temp);
			try
			{
				Dictionary<Resource, int> breakdown = new Dictionary<Resource, int>();
				for (int i = 0; i < buyers.Length; i++)
				{
					Resource resource = buyers[i].m_ResourceNeeded;
					breakdown.TryGetValue(resource, out int count);
					breakdown[resource] = count + 1;
				}

				List<KeyValuePair<Resource, int>> top = new List<KeyValuePair<Resource, int>>(breakdown);
				top.Sort((a, b) => b.Value.CompareTo(a.Value));
				StringBuilder sb = new StringBuilder();
				sb.Append("[Diag] ResourceBuyer total=").Append(buyers.Length).Append(" breakdown=");
				for (int i = 0; i < math.min(top.Count, 5); i++)
				{
					sb.Append(top[i].Key.ToString()).Append(':').Append(top[i].Value).Append(',');
				}

				Mod.log.Info(sb.ToString());
			}
			finally
			{
				buyers.Dispose();
			}
		}
	}
}

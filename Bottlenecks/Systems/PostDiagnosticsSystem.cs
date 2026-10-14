using System;
using System.Collections.Generic;
using System.Text;
using Colossal.Entities;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using Game;
using Game.Areas;
using Game.Buildings;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;

namespace Bottlenecks.Systems
{
	// Opt-in: periodically logs the state of every post facility plus mail request totals, to find out
	// why post offices stop collecting/delivering.
	public partial class PostDiagnosticsSystem : GameSystemBase
	{
		private EntityQuery m_PostFacilityQuery;
		private EntityQuery m_PostVanRequestQuery;
		private EntityQuery m_MailTransferRequestQuery;
		private EntityQuery m_MailProducerQuery;
		private EntityQuery m_PostConfigurationQuery;
		private EntityQuery m_PostVanQuery;
		private EntityQuery m_DeliveryTruckQuery;

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();
			m_PostFacilityQuery = GetEntityQuery(
				ComponentType.ReadOnly<Game.Buildings.PostFacility>(),
				ComponentType.ReadOnly<PrefabRef>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Destroyed>(),
				ComponentType.Exclude<Temp>());
			m_PostVanRequestQuery = GetEntityQuery(
				ComponentType.ReadOnly<PostVanRequest>(),
				ComponentType.ReadOnly<ServiceRequest>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
			m_MailTransferRequestQuery = GetEntityQuery(
				ComponentType.ReadOnly<MailTransferRequest>(),
				ComponentType.ReadOnly<ServiceRequest>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
			m_MailProducerQuery = GetEntityQuery(
				ComponentType.ReadOnly<MailProducer>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
			m_PostConfigurationQuery = GetEntityQuery(ComponentType.ReadOnly<PostConfigurationData>());
			m_PostVanQuery = GetEntityQuery(
				ComponentType.ReadOnly<Game.Vehicles.PostVan>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
			m_DeliveryTruckQuery = GetEntityQuery(
				ComponentType.ReadOnly<Game.Vehicles.DeliveryTruck>(),
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
			LogPostFacilities(prefabSystem);
			LogPostVanRequests();
			LogMailTransferRequests(prefabSystem);
			LogMailTrucks(prefabSystem);
			LogMailProducers();
			LogPostServiceLocked();
			LogPostVanVehicles();
		}

		private void LogPostFacilities(PrefabSystem prefabSystem)
		{
			NativeArray<Entity> entities = m_PostFacilityQuery.ToEntityArray(Allocator.Temp);
			NativeArray<Game.Buildings.PostFacility> facilities = m_PostFacilityQuery.ToComponentDataArray<Game.Buildings.PostFacility>(Allocator.Temp);
			NativeArray<PrefabRef> prefabRefs = m_PostFacilityQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
			try
			{
				for (int i = 0; i < entities.Length; i++)
				{
					try
					{
						LogOnePostFacility(prefabSystem, entities[i], facilities[i], prefabRefs[i]);
					}
					catch (Exception ex)
					{
						Mod.log.Info("[Diag] PostFacility entity " + entities[i].Index + " failed to report: " + ex.Message);
					}
				}
			}
			finally
			{
				entities.Dispose();
				facilities.Dispose();
				prefabRefs.Dispose();
			}
		}

		private void LogOnePostFacility(PrefabSystem prefabSystem, Entity entity, Game.Buildings.PostFacility facility, PrefabRef prefabRef)
		{
			StringBuilder sb = new StringBuilder();
			sb.Append("[Diag] PostFacility idx=").Append(entity.Index);
			sb.Append(" prefab=").Append(prefabSystem.GetPrefabName(prefabRef.m_Prefab));

			PostFacilityData data = default;
			if (EntityManager.HasComponent<PostFacilityData>(prefabRef.m_Prefab))
			{
				data = EntityManager.GetComponentData<PostFacilityData>(prefabRef.m_Prefab);
				sb.Append(" kind=").Append(data.m_SortingRate > 0 ? "sorting" : "office");
				sb.Append(" postVanCap=").Append(data.m_PostVanCapacity);
				sb.Append(" postTruckCap=").Append(data.m_PostTruckCapacity);
				sb.Append(" mailCap=").Append(data.m_MailCapacity);
				sb.Append(" sortingRate=").Append(data.m_SortingRate);
			}
			else
			{
				sb.Append(" kind=unknown");
			}

			sb.Append(" flags=").Append(facility.m_Flags.ToString());
			sb.Append(" acceptPri=").Append(facility.m_AcceptMailPriority);
			sb.Append(" deliverPri=").Append(facility.m_DeliverMailPriority);
			sb.Append(" processingFactor=").Append(facility.m_ProcessingFactor);
			sb.Append(" targetReq=").Append(IsAlive(facility.m_TargetRequest) ? "alive" : "null");
			sb.Append(" mailDeliverReq=").Append(IsAlive(facility.m_MailDeliverRequest) ? "alive" : "null");
			sb.Append(" mailReceiveReq=").Append(IsAlive(facility.m_MailReceiveRequest) ? "alive" : "null");

			if (EntityManager.HasBuffer<Game.Economy.Resources>(entity))
			{
				DynamicBuffer<Game.Economy.Resources> resources = EntityManager.GetBuffer<Game.Economy.Resources>(entity, true);
				sb.Append(" unsorted=").Append(EconomyUtils.GetResources(Resource.UnsortedMail, resources));
				sb.Append(" local=").Append(EconomyUtils.GetResources(Resource.LocalMail, resources));
				sb.Append(" outgoing=").Append(EconomyUtils.GetResources(Resource.OutgoingMail, resources));
			}

			// Mirrors PostFacilityAISystem: vehicle capacity is scaled by min(efficiency, immediateEfficiency).
			float efficiency = 1f;
			float immediateEfficiency = 1f;
			if (EntityManager.HasBuffer<Efficiency>(entity))
			{
				DynamicBuffer<Efficiency> efficiencyBuf = EntityManager.GetBuffer<Efficiency>(entity, true);
				efficiency = GetEfficiency(efficiencyBuf);
				immediateEfficiency = BuildingUtils.GetImmediateEfficiency(efficiencyBuf);
				sb.Append(" efficiency=").Append(efficiency);
				sb.Append(" immediateEfficiency=").Append(immediateEfficiency);
				sb.Append(" factors=");
				for (int f = 0; f < efficiencyBuf.Length; f++)
				{
					Efficiency item = efficiencyBuf[f];
					if (item.m_Efficiency < 0.999f || item.m_Efficiency > 1.001f)
					{
						sb.Append(item.m_Factor.ToString()).Append(':').Append(item.m_Efficiency).Append(',');
					}
				}
			}

			sb.Append(" vanCapacity=").Append(BuildingUtils.GetVehicleCapacity(math.min(efficiency, immediateEfficiency), data.m_PostVanCapacity));
			sb.Append(" truckCapacity=").Append(BuildingUtils.GetVehicleCapacity(math.min(efficiency, immediateEfficiency), data.m_PostTruckCapacity));

			int vanCount = 0;
			int parkedVanCount = 0;
			int truckCount = 0;
			int parkedTruckCount = 0;
			if (EntityManager.HasBuffer<OwnedVehicle>(entity))
			{
				DynamicBuffer<OwnedVehicle> ownedVehicles = EntityManager.GetBuffer<OwnedVehicle>(entity, true);
				for (int v = 0; v < ownedVehicles.Length; v++)
				{
					Entity vehicle = ownedVehicles[v].m_Vehicle;
					bool parked = EntityManager.HasComponent<ParkedCar>(vehicle);
					if (EntityManager.HasComponent<Game.Vehicles.PostVan>(vehicle))
					{
						vanCount++;
						if (parked)
						{
							parkedVanCount++;
						}
					}
					else if (EntityManager.HasComponent<Game.Vehicles.DeliveryTruck>(vehicle))
					{
						truckCount++;
						if (parked)
						{
							parkedTruckCount++;
						}
					}
				}
			}
			sb.Append(" ownedVans=").Append(vanCount).Append('(').Append(parkedVanCount).Append(" parked)");
			sb.Append(" ownedTrucks=").Append(truckCount).Append('(').Append(parkedTruckCount).Append(" parked)");

			if (EntityManager.HasComponent<Game.Routes.MailBox>(entity))
			{
				sb.Append(" mailBox=").Append(EntityManager.GetComponentData<Game.Routes.MailBox>(entity).m_MailAmount);
			}

			int serviceDistrictCount = EntityManager.HasBuffer<ServiceDistrict>(entity) ? EntityManager.GetBuffer<ServiceDistrict>(entity, true).Length : 0;
			sb.Append(" serviceDistricts=").Append(serviceDistrictCount);

			if (EntityManager.HasComponent<Building>(entity))
			{
				Building building = EntityManager.GetComponentData<Building>(entity);
				sb.Append(" buildingFlags=").Append(building.m_Flags.ToString());
				sb.Append(" inactive=").Append(BuildingUtils.CheckOption(building, BuildingOption.Inactive));
			}

			Mod.log.Info(sb.ToString());
		}

		// Same as BuildingUtils.GetEfficiency(DynamicBuffer<Efficiency>); calling that directly fails to compile
		// here because overload resolution also sees a Span<float> overload that net48 cannot resolve.
		private static float GetEfficiency(DynamicBuffer<Efficiency> buffer)
		{
			float product = 1f;
			for (int i = 0; i < buffer.Length; i++)
			{
				product *= math.max(0f, buffer[i].m_Efficiency);
			}

			return product > 0f ? math.max(0.01f, math.round(100f * product) / 100f) : 0f;
		}

		private bool IsAlive(Entity entity)
		{
			return entity != Entity.Null && EntityManager.Exists(entity);
		}

		private void LogPostVanRequests()
		{
			NativeArray<Entity> entities = m_PostVanRequestQuery.ToEntityArray(Allocator.Temp);
			NativeArray<PostVanRequest> requests = m_PostVanRequestQuery.ToComponentDataArray<PostVanRequest>(Allocator.Temp);
			NativeArray<ServiceRequest> serviceRequests = m_PostVanRequestQuery.ToComponentDataArray<ServiceRequest>(Allocator.Temp);
			try
			{
				int failCount = 0;
				int maxFail = 0;
				int dispatchedCount = 0;
				Dictionary<PostVanRequestFlags, int> flagBreakdown = new Dictionary<PostVanRequestFlags, int>();
				for (int i = 0; i < entities.Length; i++)
				{
					if (serviceRequests[i].m_FailCount > 0)
					{
						failCount++;
					}

					if (serviceRequests[i].m_FailCount > maxFail)
					{
						maxFail = serviceRequests[i].m_FailCount;
					}

					if (EntityManager.HasComponent<Dispatched>(entities[i]))
					{
						dispatchedCount++;
					}

					PostVanRequestFlags flags = requests[i].m_Flags;
					flagBreakdown.TryGetValue(flags, out int count);
					flagBreakdown[flags] = count + 1;
				}

				StringBuilder sb = new StringBuilder();
				sb.Append("[Diag] PostVanRequest total=").Append(entities.Length);
				sb.Append(" failCount>0=").Append(failCount);
				sb.Append(" maxFail=").Append(maxFail);
				sb.Append(" dispatched=").Append(dispatchedCount);
				sb.Append(" flags=");
				foreach (KeyValuePair<PostVanRequestFlags, int> kv in flagBreakdown)
				{
					sb.Append(kv.Key.ToString()).Append(':').Append(kv.Value).Append(',');
				}

				Mod.log.Info(sb.ToString());
			}
			finally
			{
				entities.Dispose();
				requests.Dispose();
				serviceRequests.Dispose();
			}
		}

		// "OutsideConnection:<prefab>", "PostFacility:<prefab>" or "Other:<prefab>".
		private string DescribeEntity(PrefabSystem prefabSystem, Entity entity)
		{
			if (entity == Entity.Null || !EntityManager.Exists(entity))
			{
				return "missing";
			}

			string name = EntityManager.HasComponent<PrefabRef>(entity) ? prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab) : "?";
			if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(entity))
			{
				return "OutsideConnection:" + name;
			}

			return (EntityManager.HasComponent<Game.Buildings.PostFacility>(entity) ? "PostFacility:" : "Other:") + name;
		}

		// Mail trucks: who owns them (outside connection or post facility), and whether they are moving.
		private void LogMailTrucks(PrefabSystem prefabSystem)
		{
			NativeArray<Entity> trucks = m_DeliveryTruckQuery.ToEntityArray(Allocator.Temp);
			try
			{
				Dictionary<string, int[]> byOwner = new Dictionary<string, int[]>();
				for (int i = 0; i < trucks.Length; i++)
				{
					Game.Vehicles.DeliveryTruck truck = EntityManager.GetComponentData<Game.Vehicles.DeliveryTruck>(trucks[i]);
					if ((truck.m_Resource & (Resource.UnsortedMail | Resource.LocalMail | Resource.OutgoingMail)) == Resource.NoResource)
					{
						continue;
					}

					Entity owner = EntityManager.HasComponent<Owner>(trucks[i]) ? EntityManager.GetComponentData<Owner>(trucks[i]).m_Owner : Entity.Null;
					string kind = DescribeEntity(prefabSystem, owner);
					if (!byOwner.TryGetValue(kind, out int[] counts))
					{
						counts = new int[3];
						byOwner[kind] = counts;
					}

					counts[0]++;
					counts[EntityManager.HasComponent<ParkedCar>(trucks[i]) ? 1 : 2]++;
				}

				StringBuilder sb = new StringBuilder("[Diag] MailTrucks owners=");
				foreach (KeyValuePair<string, int[]> kv in byOwner)
				{
					sb.Append(kv.Key).Append(" total=").Append(kv.Value[0]).Append(" parked=").Append(kv.Value[1]).Append(" moving=").Append(kv.Value[2]).Append("; ");
				}

				Mod.log.Info(sb.ToString());
			}
			finally
			{
				trucks.Dispose();
			}
		}

		private void LogMailTransferRequests(PrefabSystem prefabSystem)
		{
			NativeArray<Entity> entities = m_MailTransferRequestQuery.ToEntityArray(Allocator.Temp);
			NativeArray<MailTransferRequest> requests = m_MailTransferRequestQuery.ToComponentDataArray<MailTransferRequest>(Allocator.Temp);
			NativeArray<ServiceRequest> serviceRequests = m_MailTransferRequestQuery.ToComponentDataArray<ServiceRequest>(Allocator.Temp);
			try
			{
				// Which kind of partner handles the transfers: sorting facilities or outside connections.
				Dictionary<string, int> handlerKinds = new Dictionary<string, int>();
				for (int i = 0; i < entities.Length; i++)
				{
					string kind = "undispatched";
					if (EntityManager.HasComponent<Dispatched>(entities[i]))
					{
						kind = DescribeEntity(prefabSystem, EntityManager.GetComponentData<Dispatched>(entities[i]).m_Handler);
					}

					handlerKinds.TryGetValue(kind, out int count);
					handlerKinds[kind] = count + 1;
				}

				StringBuilder summary = new StringBuilder("[Diag] MailTransferRequest total=").Append(entities.Length).Append(" handlers=");
				foreach (KeyValuePair<string, int> kv in handlerKinds)
				{
					summary.Append(kv.Key).Append(':').Append(kv.Value).Append(", ");
				}

				Mod.log.Info(summary.ToString());
				int limit = math.min(entities.Length, 30);
				for (int i = 0; i < limit; i++)
				{
					MailTransferRequest request = requests[i];
					Entity handler = EntityManager.HasComponent<Dispatched>(entities[i]) ? EntityManager.GetComponentData<Dispatched>(entities[i]).m_Handler : Entity.Null;
					Mod.log.Info("[Diag] MailTransferRequest idx=" + entities[i].Index +
						" facility=" + request.m_Facility.Index +
						" flags=" + request.m_Flags.ToString() +
						" amount=" + request.m_Amount +
						" priority=" + request.m_Priority +
						" fail=" + serviceRequests[i].m_FailCount +
						" handler=" + (handler != Entity.Null ? handler.Index + " (" + DescribeEntity(prefabSystem, handler) + ")" : "none"));
				}
			}
			finally
			{
				entities.Dispose();
				requests.Dispose();
				serviceRequests.Dispose();
			}
		}

		private void LogMailProducers()
		{
			int tolerance = 10;
			if (!m_PostConfigurationQuery.IsEmptyIgnoreFilter)
			{
				tolerance = m_PostConfigurationQuery.GetSingleton<PostConfigurationData>().m_MailAccumulationTolerance;
			}

			NativeArray<MailProducer> producers = m_MailProducerQuery.ToComponentDataArray<MailProducer>(Allocator.Temp);
			try
			{
				int sendingOver = 0;
				int receivingOver = 0;
				int hasRequest = 0;
				long sumSending = 0;
				long sumReceiving = 0;
				for (int i = 0; i < producers.Length; i++)
				{
					MailProducer producer = producers[i];
					sumSending += producer.m_SendingMail;
					sumReceiving += producer.receivingMail;
					if (producer.m_SendingMail >= tolerance)
					{
						sendingOver++;
					}

					if (producer.receivingMail >= tolerance)
					{
						receivingOver++;
					}

					if (producer.m_MailRequest != Entity.Null)
					{
						hasRequest++;
					}
				}

				Mod.log.Info("[Diag] MailProducer total=" + producers.Length +
					" sending>=tol(" + tolerance + ")=" + sendingOver +
					" receiving>=tol=" + receivingOver +
					" hasRequest=" + hasRequest +
					" sumSending=" + sumSending +
					" sumReceiving=" + sumReceiving);
			}
			finally
			{
				producers.Dispose();
			}
		}

		private void LogPostServiceLocked()
		{
			if (m_PostConfigurationQuery.IsEmptyIgnoreFilter)
			{
				Mod.log.Info("[Diag] PostConfigurationData absent");
				return;
			}

			PostConfigurationData config = m_PostConfigurationQuery.GetSingleton<PostConfigurationData>();
			bool locked = EntityManager.HasEnabledComponent<Locked>(config.m_PostServicePrefab);
			Mod.log.Info("[Diag] postServicePrefab=" + config.m_PostServicePrefab.Index + " locked=" + locked);
		}

		private void LogPostVanVehicles()
		{
			NativeArray<Entity> vans = m_PostVanQuery.ToEntityArray(Allocator.Temp);
			try
			{
				int parked = 0;
				for (int i = 0; i < vans.Length; i++)
				{
					if (EntityManager.HasComponent<ParkedCar>(vans[i]))
					{
						parked++;
					}
				}

				Mod.log.Info("[Diag] PostVan vehicles total=" + vans.Length + " parked=" + parked + " moving=" + (vans.Length - parked));
			}
			finally
			{
				vans.Dispose();
			}
		}
	}
}

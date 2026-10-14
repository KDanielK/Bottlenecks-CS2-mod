using Game;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	// Testing tool triggered from the settings page: resets cargo transfers after a backlog, e.g. one caused
	// by a broken road network. It clears the truck trip queues (TripNeeded) of cargo stations, harbors and
	// cargo airports, and the pending StorageTransferRequest buffers of ALL storage buildings. Both ends of
	// every transfer are cleared together, so no receiver keeps waiting for goods that will never come;
	// storage buildings then decide again what to buy and sell from their actual stock.
	public partial class CargoQueueTestSystem : GameSystemBase
	{
		private static bool s_ResetRequested;

		private EntityQuery m_StationQuery;
		private EntityQuery m_TransferRequestQuery;

		public static void RequestReset()
		{
			s_ResetRequested = true;
		}

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();
			m_StationQuery = GetEntityQuery(
				ComponentType.ReadOnly<Game.Buildings.CargoTransportStation>(),
				ComponentType.ReadOnly<StorageCompany>(),
				ComponentType.ReadWrite<TripNeeded>(),
				ComponentType.Exclude<Game.Objects.OutsideConnection>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
			m_TransferRequestQuery = GetEntityQuery(
				ComponentType.ReadWrite<StorageTransferRequest>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
		}

		[Preserve]
		protected override void OnUpdate()
		{
			if (!s_ResetRequested)
			{
				return;
			}

			s_ResetRequested = false;

			// Truck trip queues at cargo stations. Goods of queued storage transfers were already taken out
			// of the station's stock when the trip was queued, so they are lost (same as vanilla clearing it).
			NativeArray<Entity> stations = m_StationQuery.ToEntityArray(Allocator.Temp);
			int stationCount = 0;
			long tripCount = 0;
			long tripGoods = 0;
			for (int i = 0; i < stations.Length; i++)
			{
				DynamicBuffer<TripNeeded> trips = EntityManager.GetBuffer<TripNeeded>(stations[i]);
				if (trips.Length == 0)
				{
					continue;
				}

				stationCount++;
				tripCount += trips.Length;
				for (int t = 0; t < trips.Length; t++)
				{
					if (trips[t].m_Purpose == Purpose.StorageTransfer)
					{
						tripGoods += trips[t].m_Data;
					}
				}

				trips.Clear();
			}

			stations.Dispose();

			// Pending transfer requests at every storage building (stations, warehouses, outside connections).
			NativeArray<Entity> holders = m_TransferRequestQuery.ToEntityArray(Allocator.Temp);
			int holderCount = 0;
			long requestCount = 0;
			for (int i = 0; i < holders.Length; i++)
			{
				DynamicBuffer<StorageTransferRequest> requests = EntityManager.GetBuffer<StorageTransferRequest>(holders[i]);
				if (requests.Length == 0)
				{
					continue;
				}

				holderCount++;
				requestCount += requests.Length;
				requests.Clear();
			}

			holders.Dispose();

			Mod.log.Info($"[Test] Cargo transfer reset: cleared {tripCount} queued trips at {stationCount} cargo stations (about {tripGoods} units of goods lost) and {requestCount} transfer requests at {holderCount} storage buildings.");
		}
	}
}

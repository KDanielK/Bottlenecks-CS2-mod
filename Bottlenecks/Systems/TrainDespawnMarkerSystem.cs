using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Notifications;
using Game.Objects;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	public partial class TrainDespawnMarkerSystem : GameSystemBase
	{
		private const uint kMarkerLifetimeFrames = 21600;

		private EntityQuery m_Query;
		private SimulationSystem m_SimulationSystem;
		private IconCommandSystem m_IconCommandSystem;

		private readonly Dictionary<Entity, uint> m_Markers = new Dictionary<Entity, uint>();

		public TrainDespawnMarkerSystem()
		{
		}

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();

			m_Query = GetEntityQuery(
				ComponentType.ReadOnly<Train>(),
				ComponentType.ReadOnly<PathOwner>(),
				ComponentType.ReadOnly<Deleted>(),
				ComponentType.ReadOnly<Transform>(),
				ComponentType.ReadOnly<TrainCurrentLane>(),
				ComponentType.Exclude<Temp>());

			m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
			m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
		}

		[Preserve]
		protected override void OnUpdate()
		{
			if (!BottleneckIconPrefabs.TryInitialize(World))
			{
				return;
			}

			uint frame = m_SimulationSystem.frameIndex;
			IconCommandBuffer cmd = default;
			bool haveCmd = false;

			if (!m_Query.IsEmptyIgnoreFilter)
			{
				NativeArray<PathOwner> pathOwners = m_Query.ToComponentDataArray<PathOwner>(Allocator.Temp);
				NativeArray<Transform> transforms = m_Query.ToComponentDataArray<Transform>(Allocator.Temp);
				NativeArray<TrainCurrentLane> lanes = m_Query.ToComponentDataArray<TrainCurrentLane>(Allocator.Temp);
				NativeArray<Entity> entities = m_Query.ToEntityArray(Allocator.Temp);

				int despawnedCount = 0;
				float3 firstPosition = default;

				for (int i = 0; i < pathOwners.Length; i++)
				{
					if ((pathOwners[i].m_State & PathFlags.Stuck) == 0)
					{
						continue;
					}

					// Empty trains that got stuck right after leaving their yard despawn silently, as in vanilla.
					// (Passengers and cargo are still attached here: Modification4 runs before ObjectEmergeSystem.)
					if (TrainChecks.IsEmptyAndFreshFromYard(EntityManager, entities[i]))
					{
						continue;
					}

					Transform transform = transforms[i];
					TrainCurrentLane lane = lanes[i];

					Entity owner = lane.m_Front.m_Lane;
					if (owner != Entity.Null && EntityManager.HasComponent<Owner>(owner))
					{
						Owner ownerData = EntityManager.GetComponentData<Owner>(owner);
						if (ownerData.m_Owner != Entity.Null
							&& EntityManager.Exists(ownerData.m_Owner)
							&& !EntityManager.HasComponent<Deleted>(ownerData.m_Owner))
						{
							owner = ownerData.m_Owner;
						}
					}

					float3 location = transform.m_Position + new float3(0f, 5f, 0f);

					if (!haveCmd)
					{
						cmd = m_IconCommandSystem.CreateCommandBuffer();
						haveCmd = true;
					}

					cmd.Add(owner, BottleneckIconPrefabs.TrainDespawned, location, IconPriority.Problem, IconClusterLayer.Default, IconFlags.IgnoreTarget);
					m_Markers[owner] = frame + kMarkerLifetimeFrames;

					despawnedCount++;
					if (despawnedCount == 1)
					{
						firstPosition = transform.m_Position;
					}

					Mod.log.Info($"TrainDespawnMarkerSystem: stuck train despawned at {transform.m_Position}");
				}

				pathOwners.Dispose();
				transforms.Dispose();
				lanes.Dispose();
				entities.Dispose();

				if (despawnedCount > 0)
				{
					DeadlockAlerts.Report(World, despawnedCount, firstPosition, false);
				}
			}

			if (m_Markers.Count > 0)
			{
				List<Entity> expired = null;
				foreach (KeyValuePair<Entity, uint> pair in m_Markers)
				{
					if (frame >= pair.Value || !EntityManager.Exists(pair.Key))
					{
						if (expired == null)
						{
							expired = new List<Entity>();
						}

						expired.Add(pair.Key);
					}
				}

				if (expired != null)
				{
					foreach (Entity entity in expired)
					{
						if (!haveCmd)
						{
							cmd = m_IconCommandSystem.CreateCommandBuffer();
							haveCmd = true;
						}

						cmd.Remove(entity, BottleneckIconPrefabs.TrainDespawned);
						m_Markers.Remove(entity);
					}
				}
			}
		}

		protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
		{
			base.OnGameLoadingComplete(purpose, mode);
			m_Markers.Clear();
			BottleneckIconPrefabs.OnGameLoadingComplete();
		}
	}
}

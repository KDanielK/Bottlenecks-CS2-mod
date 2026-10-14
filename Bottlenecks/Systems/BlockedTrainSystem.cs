using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Notifications;
using Game.Pathfind;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	public partial class BlockedTrainSystem : GameSystemBase
	{
		private const uint kWarnAfterFrames = 2700;
		private const uint kQueueWarnAfterFrames = 5400;
		private const int kMaxChainLength = 32;

		private EntityQuery m_Query;
		private SimulationSystem m_SimulationSystem;
		private IconCommandSystem m_IconCommandSystem;

		private readonly Dictionary<Entity, uint> m_BlockedSince = new Dictionary<Entity, uint>();
		private readonly Dictionary<Entity, Entity> m_IconShown = new Dictionary<Entity, Entity>();

		public BlockedTrainSystem()
		{
		}

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();

			m_Query = GetEntityQuery(
				ComponentType.ReadOnly<Train>(),
				ComponentType.ReadOnly<PathOwner>(),
				ComponentType.ReadOnly<Blocker>(),
				ComponentType.ReadOnly<TrainCurrentLane>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>(),
				ComponentType.Exclude<ParkedTrain>());

			m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
			m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
		}

		public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

		[Preserve]
		protected override void OnUpdate()
		{
			if (!BottleneckIconPrefabs.TryInitialize(World))
			{
				return;
			}

			uint frame = m_SimulationSystem.frameIndex;

			NativeArray<Entity> entities = m_Query.ToEntityArray(Allocator.Temp);
			NativeArray<Blocker> blockers = m_Query.ToComponentDataArray<Blocker>(Allocator.Temp);

			IconCommandBuffer cmd = default;
			bool haveCmd = false;

			HashSet<Entity> blockedNow = new HashSet<Entity>();

			for (int i = 0; i < entities.Length; i++)
			{
				Entity entity = entities[i];
				Blocker blocker = blockers[i];

				bool held = DeadlockRegistry.Held.Contains(entity);

				if (!held && !IsBlocked(entity, blocker))
				{
					continue;
				}

				blockedNow.Add(entity);

				if (!m_BlockedSince.TryGetValue(entity, out uint firstBlocked))
				{
					m_BlockedSince[entity] = frame;
				}
				else
				{
					bool queued = held ? false : IsQueuedForStation(entity, blocker);
					Entity desiredPrefab = held ? BottleneckIconPrefabs.TrainDespawned : (queued ? BottleneckIconPrefabs.TrainQueued : BottleneckIconPrefabs.TrainBlocked);
					uint threshold = held ? 0u : (queued ? kQueueWarnAfterFrames : kWarnAfterFrames);
					uint blockedFor = frame - firstBlocked;

					if (blockedFor >= threshold)
					{
						if (m_IconShown.TryGetValue(entity, out Entity shownPrefab))
						{
							if (shownPrefab == desiredPrefab)
							{
								continue;
							}

							if (!haveCmd)
							{
								cmd = m_IconCommandSystem.CreateCommandBuffer();
								haveCmd = true;
							}

							cmd.Remove(entity, shownPrefab);
						}

						if (!haveCmd)
						{
							cmd = m_IconCommandSystem.CreateCommandBuffer();
							haveCmd = true;
						}

						IconPriority priority = held ? IconPriority.MajorProblem : (queued ? IconPriority.Info : IconPriority.Warning);
						cmd.Add(entity, desiredPrefab, priority, IconClusterLayer.Default);
						m_IconShown[entity] = desiredPrefab;

						if (held)
						{
							Mod.log.Info($"BlockedTrainSystem: train {entity} deadlock icon added.");
						}
						else
						{
							Mod.log.Info($"BlockedTrainSystem: train {entity} blocked by {blocker.m_Blocker} ({blocker.m_Type}) for {blockedFor} frames, {(queued ? "queue warning added" : "warning added")}.");
						}
					}
					else if (m_IconShown.TryGetValue(entity, out Entity shownPrefab) && shownPrefab != desiredPrefab)
					{
						if (!haveCmd)
						{
							cmd = m_IconCommandSystem.CreateCommandBuffer();
							haveCmd = true;
						}

						cmd.Remove(entity, shownPrefab);
						m_IconShown.Remove(entity);
					}
				}
			}

			entities.Dispose();
			blockers.Dispose();

			List<Entity> toDrop = null;
			foreach (KeyValuePair<Entity, uint> pair in m_BlockedSince)
			{
				if (!blockedNow.Contains(pair.Key))
				{
					if (toDrop == null)
					{
						toDrop = new List<Entity>();
					}

					toDrop.Add(pair.Key);
				}
			}

			if (toDrop != null)
			{
				foreach (Entity entity in toDrop)
				{
					if (m_IconShown.TryGetValue(entity, out Entity shownPrefab))
					{
						m_IconShown.Remove(entity);

						if (!haveCmd)
						{
							cmd = m_IconCommandSystem.CreateCommandBuffer();
							haveCmd = true;
						}

						cmd.Remove(entity, shownPrefab);
					}

					m_BlockedSince.Remove(entity);
				}
			}
		}

		private bool IsBlocked(Entity entity, Blocker blocker)
		{
			if (blocker.m_Blocker == Entity.Null)
			{
				return false;
			}

			if (blocker.m_Type == BlockerType.None || blocker.m_Type == BlockerType.Temporary)
			{
				return false;
			}

			if (blocker.m_MaxSpeed >= 6)
			{
				return false;
			}

			if (IsBoarding(entity))
			{
				return false;
			}

			return true;
		}

		private bool IsBoarding(Entity entity)
		{
			if (EntityManager.HasComponent<PublicTransport>(entity))
			{
				PublicTransport transport = EntityManager.GetComponentData<PublicTransport>(entity);
				if ((transport.m_State & PublicTransportFlags.Boarding) != 0)
				{
					return true;
				}
			}

			if (EntityManager.HasComponent<CargoTransport>(entity))
			{
				CargoTransport transport = EntityManager.GetComponentData<CargoTransport>(entity);
				if ((transport.m_State & CargoTransportFlags.Boarding) != 0)
				{
					return true;
				}
			}

			return false;
		}

		private bool IsQueuedForStation(Entity controller, Blocker blocker)
		{
			Entity current = blocker.m_Blocker;
			if (current == Entity.Null)
			{
				return false;
			}

			List<Entity> visited = new List<Entity>(kMaxChainLength);
			visited.Add(controller);

			for (int i = 0; i < kMaxChainLength; i++)
			{
				Entity resolved = ResolveController(current);

				if (resolved == Entity.Null || !EntityManager.Exists(resolved) || resolved == controller || visited.Contains(resolved))
				{
					return false;
				}

				visited.Add(resolved);

				if (IsBoarding(resolved))
				{
					return true;
				}

				if (!EntityManager.HasComponent<Blocker>(resolved))
				{
					return false;
				}

				Blocker next = EntityManager.GetComponentData<Blocker>(resolved);
				if (next.m_Blocker == Entity.Null)
				{
					return false;
				}

				current = next.m_Blocker;
			}

			return false;
		}

		private Entity ResolveController(Entity entity)
		{
			if (entity == Entity.Null || !EntityManager.Exists(entity))
			{
				return Entity.Null;
			}

			if (EntityManager.HasComponent<Controller>(entity))
			{
				Controller data = EntityManager.GetComponentData<Controller>(entity);
				if (data.m_Controller != Entity.Null)
				{
					return data.m_Controller;
				}
			}

			return entity;
		}

		protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
		{
			base.OnGameLoadingComplete(purpose, mode);
			m_BlockedSince.Clear();
			m_IconShown.Clear();
			BottleneckIconPrefabs.OnGameLoadingComplete();
		}
	}
}

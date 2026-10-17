using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Game;
using Game.Common;
using Game.Objects;
using Game.Pathfind;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	public partial class DeadlockHoldSystem : GameSystemBase
	{
		// 30 s at 1x: a held train must stay free this long before it is released.
		private const uint kReleaseAfterFrames = 1800;

		private EntityQuery m_Query;
		private Game.Simulation.SimulationSystem m_SimulationSystem;

		public DeadlockHoldSystem()
		{
		}

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();

			m_SimulationSystem = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();
			m_Query = GetEntityQuery(
				ComponentType.ReadOnly<Train>(),
				ComponentType.ReadOnly<PathOwner>(),
				ComponentType.ReadOnly<Blocker>(),
				ComponentType.ReadOnly<TrainCurrentLane>(),
				ComponentType.ReadOnly<Transform>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>(),
				ComponentType.Exclude<ParkedTrain>());
		}

		public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

		public override int GetUpdateOffset(SystemUpdatePhase phase) => 3;

		[Preserve]
		protected override void OnUpdate()
		{
			if (Mod.Settings == null || !Mod.Settings.HoldDeadlockedTrains)
			{
				if (DeadlockRegistry.Held.Count > 0)
				{
					DeadlockRegistry.Clear();
				}

				return;
			}

			NativeArray<Entity> entities = m_Query.ToEntityArray(Allocator.Temp);
			NativeArray<PathOwner> pathOwners = m_Query.ToComponentDataArray<PathOwner>(Allocator.Temp);
			NativeArray<Transform> transforms = m_Query.ToComponentDataArray<Transform>(Allocator.Temp);
			NativeArray<Blocker> blockers = m_Query.ToComponentDataArray<Blocker>(Allocator.Temp);

			int newlyHeld = 0;
			float3 firstPosition = default;

			for (int i = 0; i < entities.Length; i++)
			{
				Entity entity = entities[i];
				PathOwner pathOwner = pathOwners[i];

				if ((pathOwner.m_State & PathFlags.Stuck) == 0)
				{
					continue;
				}

				// Blocked by a parked train (depot/yard track): vanilla removes the parked train and the
				// moving train continues, so leave the flag for TransportTrainAISystem to handle.
				if (EntityManager.HasComponent<ParkedTrain>(blockers[i].m_Blocker))
				{
					continue;
				}

				// Empty train that just left its yard: let vanilla despawn it, as players expect.
				if (TrainChecks.IsEmptyAndFreshFromYard(EntityManager, entity))
				{
					continue;
				}

				pathOwner.m_State &= ~PathFlags.Stuck;
				EntityManager.SetComponentData(entity, pathOwner);

				if (DeadlockRegistry.Held.Add(entity))
				{
					newlyHeld++;
					if (newlyHeld == 1)
					{
						firstPosition = transforms[i].m_Position;
					}
				}
			}

			entities.Dispose();
			pathOwners.Dispose();
			transforms.Dispose();
			blockers.Dispose();

			DropResolved(m_SimulationSystem.frameIndex);

			if (newlyHeld > 0)
			{
				DeadlockAlerts.Report(World, newlyHeld, firstPosition, true);
			}
		}

		private void DropResolved(uint frame)
		{
			if (DeadlockRegistry.Held.Count == 0)
			{
				return;
			}

			List<Entity> toDrop = null;
			foreach (Entity entity in DeadlockRegistry.Held)
			{
				if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Deleted>(entity))
				{
					if (toDrop == null)
					{
						toDrop = new List<Entity>();
					}

					toDrop.Add(entity);
					continue;
				}

				if (EntityManager.HasComponent<Blocker>(entity))
				{
					Blocker blocker = EntityManager.GetComponentData<Blocker>(entity);
					if (blocker.m_Blocker == Entity.Null || blocker.m_MaxSpeed >= 6)
					{
						if (!DeadlockRegistry.FreeSince.TryGetValue(entity, out uint since))
						{
							DeadlockRegistry.FreeSince[entity] = frame;
						}
						else if (frame - since >= kReleaseAfterFrames)
						{
							if (toDrop == null)
							{
								toDrop = new List<Entity>();
							}

							toDrop.Add(entity);
						}
					}
					else
					{
						DeadlockRegistry.FreeSince.Remove(entity);
					}
				}
			}

			if (toDrop != null)
			{
				foreach (Entity entity in toDrop)
				{
					DeadlockRegistry.Held.Remove(entity);
					DeadlockRegistry.FreeSince.Remove(entity);
				}
			}
		}

		protected override void OnGameLoadingComplete(Purpose purpose, GameMode mode)
		{
			base.OnGameLoadingComplete(purpose, mode);
			DeadlockRegistry.Clear();
		}
	}
}

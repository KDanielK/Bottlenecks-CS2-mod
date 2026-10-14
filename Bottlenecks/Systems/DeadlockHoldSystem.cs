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
		private EntityQuery m_Query;

		public DeadlockHoldSystem()
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

			DropResolved();

			if (newlyHeld > 0)
			{
				DeadlockAlerts.Report(World, newlyHeld, firstPosition, true);
			}
		}

		private void DropResolved()
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
						if (toDrop == null)
						{
							toDrop = new List<Entity>();
						}

						toDrop.Add(entity);
					}
				}
			}

			if (toDrop != null)
			{
				foreach (Entity entity in toDrop)
				{
					DeadlockRegistry.Held.Remove(entity);
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

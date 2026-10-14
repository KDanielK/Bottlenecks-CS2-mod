using System.Collections.Generic;
using Game;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Notifications;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	// Shows a blue/orange/red icon on cargo stations, harbors and cargo airports whose truck queue has
	// grown too long, and a notification card when a station becomes backlogged.
	public partial class CargoStationOverloadSystem : GameSystemBase
	{
		private const int kLevel1OnQueue = 10;
		private const uint kLevel1OnFrames = 10800;
		private const int kLevel1ClearQueue = 8;

		private const int kLevel2OnRounds = 4;
		private const uint kLevel2OnFrames = 3600;
		private const int kLevel2ClearRounds = 3;

		private const int kLevel3OnRounds = 50;
		private const uint kLevel3OnFrames = 3600;
		private const int kLevel3ClearRounds = 40;

		private struct StationState
		{
			public bool Level1On;
			public bool Level2On;
			public bool Level3On;
			public uint Level1Since;
			public uint Level2Since;
			public uint Level3Since;
			public bool Level1Shown;
			public bool Level2Shown;
			public bool Level3Shown;
			public int ShownLevel;
			public Entity ShownPrefab;
		}

		private EntityQuery m_Query;
		private SimulationSystem m_SimulationSystem;
		private IconCommandSystem m_IconCommandSystem;
		private PrefabSystem m_PrefabSystem;

		private readonly Dictionary<Entity, StationState> m_States = new Dictionary<Entity, StationState>();

		public CargoStationOverloadSystem()
		{
		}

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();

			m_Query = GetEntityQuery(
				ComponentType.ReadOnly<Game.Buildings.CargoTransportStation>(),
				ComponentType.ReadOnly<Game.Companies.StorageCompany>(),
				ComponentType.ReadOnly<PrefabRef>(),
				ComponentType.Exclude<Game.Objects.OutsideConnection>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());

			m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
			m_IconCommandSystem = World.GetOrCreateSystemManaged<IconCommandSystem>();
			m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
		}

		public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

		[Preserve]
		protected override void OnUpdate()
		{
			if (!BottleneckIconPrefabs.TryInitialize(World))
			{
				return;
			}

			if (Mod.Settings != null && !Mod.Settings.ShowCargoStationIcons)
			{
				RemoveAllIcons();
				return;
			}

			uint frame = m_SimulationSystem.frameIndex;

			NativeArray<Entity> entities = m_Query.ToEntityArray(Allocator.Temp);
			NativeArray<PrefabRef> prefabRefs = m_Query.ToComponentDataArray<PrefabRef>(Allocator.Temp);

			IconCommandBuffer cmd = default;
			bool haveCmd = false;

			HashSet<Entity> seen = new HashSet<Entity>();

			for (int i = 0; i < entities.Length; i++)
			{
				Entity entity = entities[i];
				PrefabRef prefabRef = prefabRefs[i];
				seen.Add(entity);

				int queueLength = EntityManager.HasBuffer<TripNeeded>(entity) ? EntityManager.GetBuffer<TripNeeded>(entity, true).Length : 0;

				int maxTransports = 1;
				if (EntityManager.HasComponent<TransportCompanyData>(prefabRef.m_Prefab))
				{
					maxTransports = EntityManager.GetComponentData<TransportCompanyData>(prefabRef.m_Prefab).m_MaxTransports;
				}

				int rounds = queueLength / math.max(1, maxTransports);

				StationState state;
				if (!m_States.TryGetValue(entity, out state))
				{
					state = default;
				}

				bool level1On = queueLength >= kLevel1OnQueue;
				bool level2On = rounds > kLevel2OnRounds;
				bool level3On = rounds > kLevel3OnRounds;

				UpdateOnFlag(level1On, ref state.Level1On, ref state.Level1Since, frame);
				UpdateOnFlag(level2On, ref state.Level2On, ref state.Level2Since, frame);
				UpdateOnFlag(level3On, ref state.Level3On, ref state.Level3Since, frame);

				UpdateShown(state.Level1On, state.Level1Since, kLevel1OnFrames, frame, queueLength < kLevel1ClearQueue, ref state.Level1Shown);
				UpdateShown(state.Level2On, state.Level2Since, kLevel2OnFrames, frame, rounds < kLevel2ClearRounds, ref state.Level2Shown);
				UpdateShown(state.Level3On, state.Level3Since, kLevel3OnFrames, frame, rounds < kLevel3ClearRounds, ref state.Level3Shown);

				int newLevel = state.Level3Shown ? 3 : (state.Level2Shown ? 2 : (state.Level1Shown ? 1 : 0));

				if (newLevel != state.ShownLevel)
				{
					if (state.ShownPrefab != Entity.Null)
					{
						if (!haveCmd)
						{
							cmd = m_IconCommandSystem.CreateCommandBuffer();
							haveCmd = true;
						}

						cmd.Remove(entity, state.ShownPrefab);
						state.ShownPrefab = Entity.Null;
					}

					if (newLevel != 0)
					{
						Entity prefab = GetPrefabForLevel(newLevel);
						IconPriority priority = GetPriorityForLevel(newLevel);
						if (!haveCmd)
						{
							cmd = m_IconCommandSystem.CreateCommandBuffer();
							haveCmd = true;
						}

						cmd.Add(entity, prefab, priority, IconClusterLayer.Default);
						state.ShownPrefab = prefab;
					}

					string prefabName = m_PrefabSystem.GetPrefabName(prefabRef.m_Prefab);
					Mod.log.Info($"CargoStationOverloadSystem: station {entity.Index} ({prefabName}) queue={queueLength} maxTransports={maxTransports} rounds={rounds} level {state.ShownLevel}->{newLevel}");

					int oldLevel = state.ShownLevel;
					state.ShownLevel = newLevel;

					if (newLevel == 3 && oldLevel != 3)
					{
						float3 position = default;
						if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
						{
							position = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
						}

						DeadlockAlerts.ReportCargoBacklog(World, prefabName, queueLength, position);
					}
				}

				m_States[entity] = state;
			}

			entities.Dispose();
			prefabRefs.Dispose();

			DropGoneStations(seen, ref cmd, ref haveCmd);
		}

		private static void UpdateOnFlag(bool on, ref bool wasOn, ref uint since, uint frame)
		{
			if (on)
			{
				if (!wasOn)
				{
					wasOn = true;
					since = frame;
				}
			}
			else
			{
				wasOn = false;
			}
		}

		private static void UpdateShown(bool on, uint since, uint onFrames, uint frame, bool clear, ref bool shown)
		{
			if (!shown)
			{
				if (on && frame - since >= onFrames)
				{
					shown = true;
				}
			}
			else if (clear)
			{
				shown = false;
			}
		}

		private static Entity GetPrefabForLevel(int level)
		{
			switch (level)
			{
				case 1: return BottleneckIconPrefabs.CargoNotSelling;
				case 2: return BottleneckIconPrefabs.CargoOverloaded;
				default: return BottleneckIconPrefabs.CargoBacklogged;
			}
		}

		private static IconPriority GetPriorityForLevel(int level)
		{
			switch (level)
			{
				case 1: return IconPriority.Info;
				case 2: return IconPriority.Warning;
				default: return IconPriority.MajorProblem;
			}
		}

		private void RemoveAllIcons()
		{
			if (m_States.Count == 0)
			{
				return;
			}

			IconCommandBuffer cmd = m_IconCommandSystem.CreateCommandBuffer();
			foreach (KeyValuePair<Entity, StationState> pair in m_States)
			{
				if (pair.Value.ShownPrefab != Entity.Null)
				{
					cmd.Remove(pair.Key, pair.Value.ShownPrefab);
				}
			}

			m_States.Clear();
		}

		private void DropGoneStations(HashSet<Entity> seen, ref IconCommandBuffer cmd, ref bool haveCmd)
		{
			if (m_States.Count == 0)
			{
				return;
			}

			List<Entity> toDrop = null;
			foreach (KeyValuePair<Entity, StationState> pair in m_States)
			{
				if (!seen.Contains(pair.Key))
				{
					if (toDrop == null)
					{
						toDrop = new List<Entity>();
					}

					toDrop.Add(pair.Key);
				}
			}

			if (toDrop == null)
			{
				return;
			}

			foreach (Entity entity in toDrop)
			{
				StationState state = m_States[entity];
				if (state.ShownPrefab != Entity.Null)
				{
					if (!haveCmd)
					{
						cmd = m_IconCommandSystem.CreateCommandBuffer();
						haveCmd = true;
					}

					cmd.Remove(entity, state.ShownPrefab);
				}

				m_States.Remove(entity);
			}
		}

		protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
		{
			base.OnGameLoadingComplete(purpose, mode);
			m_States.Clear();
		}
	}
}

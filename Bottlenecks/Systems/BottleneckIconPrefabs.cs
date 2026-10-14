using Game.Common;
using Game.Notifications;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace Bottlenecks.Systems
{
	public static class BottleneckIconPrefabs
	{
		private const string kTrainBlockedName = "Bottlenecks.TrainBlocked";
		private const string kTrainQueuedName = "Bottlenecks.TrainQueued";
		private const string kTrainDespawnedName = "Bottlenecks.TrainDespawned";

		private const string kTrainBlockedResource = "Bottlenecks.Icons.TrainBlocked.png";
		private const string kTrainQueuedResource = "Bottlenecks.Icons.TrainQueued.png";
		private const string kTrainDespawnedResource = "Bottlenecks.Icons.TrainDespawned.png";

		private const int kMaxInitAttempts = 600;

		private static bool s_Ready;
		private static int s_InitAttempts;
		private static bool s_CleanupPending;

		// Our own prefabs, created at most once per game session.
		private static bool s_OwnCreated;
		private static Entity s_OwnTrainBlocked;
		private static Entity s_OwnTrainQueued;
		private static Entity s_OwnTrainDespawned;

		private static Entity s_TrainBlocked;
		private static Entity s_TrainQueued;
		private static Entity s_TrainDespawned;

		public static bool IsReady => s_Ready;
		public static Entity TrainBlocked => s_TrainBlocked;
		public static Entity TrainQueued => s_TrainQueued;
		public static Entity TrainDespawned => s_TrainDespawned;

		public static void OnGameLoadingComplete()
		{
			s_CleanupPending = true;
		}

		// Must run in the PrefabUpdate phase before PrefabInitializeSystem: it only initializes prefabs that
		// still carry Created, and CleanUpSystem strips Created at the end of every frame. A prefab added
		// later in the frame (e.g. from a Modification or GameSimulation system) is never initialized.
		public static bool CreatePrefabs(World world)
		{
			if (s_OwnCreated)
			{
				return true;
			}

			EntityQuery iconConfigQuery = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<IconConfigurationData>());
			bool hasConfig = !iconConfigQuery.IsEmptyIgnoreFilter;
			Entity iconConfigEntity = hasConfig ? iconConfigQuery.GetSingletonEntity() : Entity.Null;
			iconConfigQuery.Dispose();
			if (!hasConfig)
			{
				return false;
			}

			PrefabSystem prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();
			IconConfigurationPrefab iconConfig = prefabSystem.GetPrefab<IconConfigurationPrefab>(iconConfigEntity);
			Texture2D reference = iconConfig != null ? iconConfig.m_MissingIcon : null;

			s_OwnTrainBlocked = CreateIconPrefab(prefabSystem, kTrainBlockedName, kTrainBlockedResource, reference);
			s_OwnTrainQueued = CreateIconPrefab(prefabSystem, kTrainQueuedName, kTrainQueuedResource, reference);
			s_OwnTrainDespawned = CreateIconPrefab(prefabSystem, kTrainDespawnedName, kTrainDespawnedResource, reference);
			s_OwnCreated = true;
			return true;
		}

		public static bool TryInitialize(World world)
		{
			// Guard against our prefab entities becoming invalid after initialization: issuing icon
			// commands for an invalid prefab would crash the game natively during command playback.
			if (s_Ready && ((s_OwnTrainBlocked != Entity.Null && !IsInitialized(world, s_OwnTrainBlocked))
				|| (s_OwnTrainQueued != Entity.Null && !IsInitialized(world, s_OwnTrainQueued))
				|| (s_OwnTrainDespawned != Entity.Null && !IsInitialized(world, s_OwnTrainDespawned))))
			{
				Mod.log.Warn("BottleneckIconPrefabs: own icon prefab became invalid, switching to the vanilla fallback icon.");
				if (!IsInitialized(world, s_OwnTrainBlocked))
				{
					s_OwnTrainBlocked = Entity.Null;
				}

				if (!IsInitialized(world, s_OwnTrainQueued))
				{
					s_OwnTrainQueued = Entity.Null;
				}

				if (!IsInitialized(world, s_OwnTrainDespawned))
				{
					s_OwnTrainDespawned = Entity.Null;
				}

				s_Ready = false;
			}

			if (!s_Ready)
			{
				PrefabSystem prefabSystem = world.GetOrCreateSystemManaged<PrefabSystem>();

				// Prefabs are created by IconPrefabSetupSystem in the PrefabUpdate phase.
				if (!s_OwnCreated)
				{
					return false;
				}

				// PrefabInitializeSystem runs LateInitialize, which builds the icon archetype. Creating an
				// icon from a prefab without a valid archetype crashes the game natively, so wait for
				// initialization and fall back to a vanilla icon if it never happens.
				bool blockedValid = IsInitialized(world, s_OwnTrainBlocked);
				bool queuedValid = IsInitialized(world, s_OwnTrainQueued);
				bool despawnedValid = IsInitialized(world, s_OwnTrainDespawned);
				if ((!blockedValid && s_OwnTrainBlocked != Entity.Null) || (!queuedValid && s_OwnTrainQueued != Entity.Null) || (!despawnedValid && s_OwnTrainDespawned != Entity.Null))
				{
					if (++s_InitAttempts < kMaxInitAttempts)
					{
						return false;
					}

					Mod.log.Warn($"BottleneckIconPrefabs: icon prefabs were not initialized by the game after {s_InitAttempts} attempts (TrainBlocked={blockedValid}, TrainQueued={queuedValid}, TrainDespawned={despawnedValid}).");
					if (!blockedValid)
					{
						s_OwnTrainBlocked = Entity.Null;
					}

					if (!queuedValid)
					{
						s_OwnTrainQueued = Entity.Null;
					}

					if (!despawnedValid)
					{
						s_OwnTrainDespawned = Entity.Null;
					}
				}

				Entity fallback = Entity.Null;
				if (s_OwnTrainBlocked == Entity.Null || s_OwnTrainQueued == Entity.Null || s_OwnTrainDespawned == Entity.Null)
				{
					fallback = GetFallbackPrefab(world);
					if (fallback == Entity.Null)
					{
						// Police configuration not loaded yet, retry on the next update.
						return false;
					}

					Mod.log.Warn("BottleneckIconPrefabs: using the vanilla traffic accident icon for the icons that could not be created.");
				}

				s_TrainBlocked = s_OwnTrainBlocked != Entity.Null ? s_OwnTrainBlocked : fallback;
				s_TrainQueued = s_OwnTrainQueued != Entity.Null ? s_OwnTrainQueued : fallback;
				s_TrainDespawned = s_OwnTrainDespawned != Entity.Null ? s_OwnTrainDespawned : fallback;
				s_Ready = true;
				Mod.log.Info($"BottleneckIconPrefabs: ready after {s_InitAttempts} wait(s) (own TrainBlocked={s_OwnTrainBlocked != Entity.Null}, own TrainQueued={s_OwnTrainQueued != Entity.Null}, own TrainDespawned={s_OwnTrainDespawned != Entity.Null}).");
			}

			if (s_CleanupPending)
			{
				s_CleanupPending = false;
				RemoveLeftoverIcons(world);
			}

			return true;
		}

		// Icons from a previous session may have been saved in the savegame; remove them so the
		// systems can rebuild their state from scratch. Never touches vanilla (fallback) icons.
		private static void RemoveLeftoverIcons(World world)
		{
			if (s_OwnTrainBlocked == Entity.Null && s_OwnTrainQueued == Entity.Null && s_OwnTrainDespawned == Entity.Null)
			{
				return;
			}

			EntityQuery query = world.EntityManager.CreateEntityQuery(
				ComponentType.ReadOnly<Icon>(),
				ComponentType.ReadOnly<Owner>(),
				ComponentType.ReadOnly<PrefabRef>(),
				ComponentType.Exclude<Deleted>());

			if (!query.IsEmptyIgnoreFilter)
			{
				NativeArray<Owner> owners = query.ToComponentDataArray<Owner>(Allocator.Temp);
				NativeArray<PrefabRef> refs = query.ToComponentDataArray<PrefabRef>(Allocator.Temp);
				IconCommandBuffer cmd = world.GetOrCreateSystemManaged<IconCommandSystem>().CreateCommandBuffer();

				int removed = 0;
				for (int i = 0; i < owners.Length; i++)
				{
					Entity prefab = refs[i].m_Prefab;
					if (prefab != Entity.Null && (prefab == s_OwnTrainBlocked || prefab == s_OwnTrainQueued || prefab == s_OwnTrainDespawned))
					{
						cmd.Remove(owners[i].m_Owner, prefab);
						removed++;
					}
				}

				owners.Dispose();
				refs.Dispose();

				if (removed > 0)
				{
					Mod.log.Info($"BottleneckIconPrefabs: removed {removed} leftover icon(s) from the loaded save.");
				}
			}

			query.Dispose();
		}

		private static bool IsInitialized(World world, Entity prefab)
		{
			EntityManager entityManager = world.EntityManager;
			return prefab != Entity.Null
				&& entityManager.Exists(prefab)
				&& entityManager.HasComponent<NotificationIconData>(prefab)
				&& entityManager.GetComponentData<NotificationIconData>(prefab).m_Archetype.Valid;
		}

		private static Entity GetFallbackPrefab(World world)
		{
			EntityQuery query = world.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<PoliceConfigurationData>());
			Entity result = query.IsEmptyIgnoreFilter ? Entity.Null : query.GetSingleton<PoliceConfigurationData>().m_TrafficAccidentNotificationPrefab;
			query.Dispose();
			return result;
		}

		private static Entity CreateIconPrefab(PrefabSystem prefabSystem, string name, string resourceName, Texture2D reference)
		{
			Texture2D icon = Icons.IconTextures.CreateIconTexture(resourceName, reference);
			if (icon == null)
			{
				return Entity.Null;
			}

			NotificationIconPrefab prefab = ScriptableObject.CreateInstance<NotificationIconPrefab>();
			prefab.name = name;
			prefab.m_Icon = icon;
			prefab.m_Description = name;
			prefab.m_TargetDescription = name;

			if (!prefabSystem.AddPrefab(prefab))
			{
				Object.Destroy(prefab);
				Object.Destroy(icon);
				Mod.log.Error($"BottleneckIconPrefabs: AddPrefab failed for '{name}'.");
				return Entity.Null;
			}

			Entity entity = prefabSystem.GetEntity(prefab);
			if (entity == Entity.Null)
			{
				Mod.log.Error($"BottleneckIconPrefabs: GetEntity returned null for '{name}'.");
			}
			else
			{
				Mod.log.Info($"BottleneckIconPrefabs: created icon prefab '{name}'.");
			}

			return entity;
		}
	}
}

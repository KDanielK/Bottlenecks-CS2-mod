using System.Text;
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
		private const string kCargoNotSellingName = "Bottlenecks.CargoNotSelling";
		private const string kCargoOverloadedName = "Bottlenecks.CargoOverloaded";
		private const string kCargoBackloggedName = "Bottlenecks.CargoBacklogged";

		private const string kTrainBlockedResource = "Bottlenecks.Icons.TrainBlocked.png";
		private const string kTrainQueuedResource = "Bottlenecks.Icons.TrainQueued.png";
		private const string kTrainDespawnedResource = "Bottlenecks.Icons.TrainDespawned.png";
		private const string kCargoNotSellingResource = "Bottlenecks.Icons.CargoNotSelling.png";
		private const string kCargoOverloadedResource = "Bottlenecks.Icons.CargoOverloaded.png";
		private const string kCargoBackloggedResource = "Bottlenecks.Icons.CargoBacklogged.png";

		private const int kMaxInitAttempts = 600;

		private const int kTrainBlocked = 0;
		private const int kTrainQueued = 1;
		private const int kTrainDespawned = 2;
		private const int kCargoNotSelling = 3;
		private const int kCargoOverloaded = 4;
		private const int kCargoBacklogged = 5;

		private struct IconDef
		{
			public string Name;
			public string Resource;
			public Entity Own;
			public Entity Resolved;
		}

		private static readonly IconDef[] s_Icons = new IconDef[]
		{
			new IconDef { Name = kTrainBlockedName, Resource = kTrainBlockedResource },
			new IconDef { Name = kTrainQueuedName, Resource = kTrainQueuedResource },
			new IconDef { Name = kTrainDespawnedName, Resource = kTrainDespawnedResource },
			new IconDef { Name = kCargoNotSellingName, Resource = kCargoNotSellingResource },
			new IconDef { Name = kCargoOverloadedName, Resource = kCargoOverloadedResource },
			new IconDef { Name = kCargoBackloggedName, Resource = kCargoBackloggedResource },
		};

		private static bool s_Ready;
		private static int s_InitAttempts;
		private static bool s_CleanupPending;
		private static bool s_OwnCreated;

		public static bool IsReady => s_Ready;
		public static Entity TrainBlocked => s_Icons[kTrainBlocked].Resolved;
		public static Entity TrainQueued => s_Icons[kTrainQueued].Resolved;
		public static Entity TrainDespawned => s_Icons[kTrainDespawned].Resolved;
		public static Entity CargoNotSelling => s_Icons[kCargoNotSelling].Resolved;
		public static Entity CargoOverloaded => s_Icons[kCargoOverloaded].Resolved;
		public static Entity CargoBacklogged => s_Icons[kCargoBacklogged].Resolved;

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

			for (int i = 0; i < s_Icons.Length; i++)
			{
				s_Icons[i].Own = CreateIconPrefab(prefabSystem, s_Icons[i].Name, s_Icons[i].Resource, reference);
			}

			s_OwnCreated = true;
			return true;
		}

		public static bool TryInitialize(World world)
		{
			// Guard against our prefab entities becoming invalid after initialization: issuing icon
			// commands for an invalid prefab would crash the game natively during command playback.
			if (s_Ready && HasInvalidOwnPrefab(world))
			{
				Mod.log.Warn("BottleneckIconPrefabs: own icon prefab became invalid, switching to the vanilla fallback icon.");
				NullInvalidOwnPrefabs(world);
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
				bool allValid = true;
				for (int i = 0; i < s_Icons.Length; i++)
				{
					if (s_Icons[i].Own != Entity.Null && !IsInitialized(world, s_Icons[i].Own))
					{
						allValid = false;
						break;
					}
				}

				if (!allValid)
				{
					if (++s_InitAttempts < kMaxInitAttempts)
					{
						return false;
					}

					Mod.log.Warn($"BottleneckIconPrefabs: icon prefabs were not initialized by the game after {s_InitAttempts} attempts ({DescribeInitStatus(world)}).");
					NullInvalidOwnPrefabs(world);
				}

				Entity fallback = Entity.Null;
				if (AnyOwnPrefabNull())
				{
					fallback = GetFallbackPrefab(world);
					if (fallback == Entity.Null)
					{
						// Police configuration not loaded yet, retry on the next update.
						return false;
					}

					Mod.log.Warn("BottleneckIconPrefabs: using the vanilla traffic accident icon for the icons that could not be created.");
				}

				for (int i = 0; i < s_Icons.Length; i++)
				{
					s_Icons[i].Resolved = s_Icons[i].Own != Entity.Null ? s_Icons[i].Own : fallback;
				}

				s_Ready = true;
				Mod.log.Info($"BottleneckIconPrefabs: ready after {s_InitAttempts} wait(s) ({DescribeOwnStatus()}).");
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
			if (!AnyOwnPrefabCreated())
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
					if (prefab != Entity.Null && IsOwnPrefab(prefab))
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

		private static bool HasInvalidOwnPrefab(World world)
		{
			for (int i = 0; i < s_Icons.Length; i++)
			{
				if (s_Icons[i].Own != Entity.Null && !IsInitialized(world, s_Icons[i].Own))
				{
					return true;
				}
			}

			return false;
		}

		private static void NullInvalidOwnPrefabs(World world)
		{
			for (int i = 0; i < s_Icons.Length; i++)
			{
				if (s_Icons[i].Own != Entity.Null && !IsInitialized(world, s_Icons[i].Own))
				{
					s_Icons[i].Own = Entity.Null;
				}
			}
		}

		private static bool AnyOwnPrefabNull()
		{
			for (int i = 0; i < s_Icons.Length; i++)
			{
				if (s_Icons[i].Own == Entity.Null)
				{
					return true;
				}
			}

			return false;
		}

		private static bool AnyOwnPrefabCreated()
		{
			for (int i = 0; i < s_Icons.Length; i++)
			{
				if (s_Icons[i].Own != Entity.Null)
				{
					return true;
				}
			}

			return false;
		}

		private static bool IsOwnPrefab(Entity prefab)
		{
			for (int i = 0; i < s_Icons.Length; i++)
			{
				if (s_Icons[i].Own != Entity.Null && prefab == s_Icons[i].Own)
				{
					return true;
				}
			}

			return false;
		}

		private static string DescribeInitStatus(World world)
		{
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < s_Icons.Length; i++)
			{
				if (i > 0)
				{
					sb.Append(", ");
				}

				sb.Append(s_Icons[i].Name).Append('=').Append(IsInitialized(world, s_Icons[i].Own));
			}

			return sb.ToString();
		}

		private static string DescribeOwnStatus()
		{
			StringBuilder sb = new StringBuilder();
			for (int i = 0; i < s_Icons.Length; i++)
			{
				if (i > 0)
				{
					sb.Append(", ");
				}

				sb.Append("own ").Append(s_Icons[i].Name).Append('=').Append(s_Icons[i].Own != Entity.Null);
			}

			return sb.ToString();
		}
	}
}

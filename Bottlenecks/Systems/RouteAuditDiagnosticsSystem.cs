using Game;
using Game.Common;
using Game.Companies;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	// Diagnostics only: runs right after StorageTransferSystem (same interval, so the same frame) and checks every
	// finished storage transfer route it may just have turned into transfer requests. Any route that bulk freight
	// optimisation did not handle is counted and sampled, to find routes that reach the game without the swap.
	public partial class RouteAuditDiagnosticsSystem : GameSystemBase
	{
		private const uint kReportFrames = 4096;

		private EntityQuery m_Query;
		private SimulationSystem m_SimulationSystem;
		private PrefabSystem m_PrefabSystem;
		private RailFreightRerouteSystem m_Reroute;
		private uint m_LastReport;
		private int m_Finished;
		private int m_Unhandled;
		private string m_Samples = "";
		private int m_SampleCount;

		public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

		[Preserve]
		protected override void OnCreate()
		{
			base.OnCreate();
			m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
			m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();
			m_Reroute = World.GetOrCreateSystemManaged<RailFreightRerouteSystem>();
			m_Query = GetEntityQuery(
				ComponentType.ReadOnly<StorageTransfer>(),
				ComponentType.ReadOnly<PathInformation>(),
				ComponentType.Exclude<Deleted>(),
				ComponentType.Exclude<Temp>());
		}

		[Preserve]
		protected override void OnUpdate()
		{
			if (!Mod.EnableDiagnostics || Mod.Settings == null || !Mod.Settings.BulkFreightOptimisation)
			{
				return;
			}

			NativeArray<Entity> entities = m_Query.ToEntityArray(Allocator.Temp);
			for (int i = 0; i < entities.Length; i++)
			{
				PathInformation info = EntityManager.GetComponentData<PathInformation>(entities[i]);
				if ((info.m_State & PathFlags.Pending) != 0)
				{
					continue;
				}

				m_Finished++;
				if (m_Reroute.WasHandled(entities[i], info))
				{
					continue;
				}

				m_Unhandled++;
				if (m_SampleCount < 10)
				{
					m_SampleCount++;
					StorageTransfer transfer = EntityManager.GetComponentData<StorageTransfer>(entities[i]);
					int elements = EntityManager.HasBuffer<PathElement>(entities[i]) ? EntityManager.GetBuffer<PathElement>(entities[i], true).Length : -1;
					m_Samples += $" | {Name(entities[i])} {transfer.m_Resource} {transfer.m_Amount} {Name(info.m_Origin)} -> {Name(info.m_Destination)} state={info.m_State} elements={elements} frame={m_SimulationSystem.frameIndex}";
				}
			}

			entities.Dispose();

			uint frame = m_SimulationSystem.frameIndex;
			if (frame - m_LastReport >= kReportFrames)
			{
				m_LastReport = frame;
				Mod.log.Info($"[Diag] RouteAudit finishedRoutesAtStorageTransfer={m_Finished} notHandledBySwap={m_Unhandled}{m_Samples}");
				m_Finished = m_Unhandled = m_SampleCount = 0;
				m_Samples = "";
			}
		}

		private string Name(Entity entity)
		{
			if (entity == Entity.Null || !EntityManager.Exists(entity) || !EntityManager.HasComponent<PrefabRef>(entity))
			{
				return "null";
			}

			return m_PrefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab) + "#" + entity.Index;
		}
	}
}

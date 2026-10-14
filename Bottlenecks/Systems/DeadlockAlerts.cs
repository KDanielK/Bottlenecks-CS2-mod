using System;
using Game.PSI;
using Game.Rendering;
using Game.Simulation;
using Game.UI.Localization;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace Bottlenecks.Systems
{
	public static class DeadlockAlerts
	{
		public static void Report(World world, int trainCount, float3 position, bool held)
		{
			if (Mod.Settings?.ShowNotifications != false)
			{
				NotificationSystem.Push(
					"Bottlenecks.Deadlock",
					LocalizedString.Value("Train deadlock"),
					held
						? LocalizedString.Value($"{trainCount} train(s) stuck in a deadlock and held in place. Click to jump there.")
						: LocalizedString.Value($"{trainCount} stuck train(s) were despawned. Click to jump there."),
					onClicked: () =>
					{
						JumpTo(world, position);
						NotificationSystem.Pop("Bottlenecks.Deadlock");
					});
			}

			if (Mod.Settings?.PauseOnDeadlock == true)
			{
				world.GetOrCreateSystemManaged<SimulationSystem>().selectedSpeed = 0f;
			}

			Mod.log.Info($"DeadlockAlerts: {trainCount} train(s) {(held ? "held" : "despawned")} at {position}.");
		}

		private static void JumpTo(World world, float3 position)
		{
			try
			{
				CameraUpdateSystem camSys = world.GetOrCreateSystemManaged<CameraUpdateSystem>();
				var ctl = camSys.gamePlayController;
				if (ctl != null)
				{
					if (camSys.orbitCameraController != null)
					{
						camSys.orbitCameraController.followedEntity = Entity.Null;
					}

					camSys.activeCameraController = ctl;
					ctl.pivot = new Vector3(position.x, position.y, position.z);
				}
			}
			catch (Exception ex)
			{
				Mod.log.Warn($"DeadlockAlerts: failed to jump camera: {ex}");
			}
		}
	}
}

using Unity.Entities;

namespace Bottlenecks.Systems
{
	public static class TrainChecks
	{
		// Odometer is reset to 0 while a train is parked at its depot, so a short distance means the
		// train has only just left its depot or yard (or was just created).
		private const float kFreshFromYardDistance = 500f;

		// Empty trains that get stuck right after leaving a yard (e.g. spawned on top of each other) are
		// left to vanilla: the game despawns them and we show no marker or notification for them.
		public static bool IsEmptyAndFreshFromYard(EntityManager entityManager, Entity controller)
		{
			if (!entityManager.HasComponent<Game.Vehicles.Odometer>(controller)
				|| entityManager.GetComponentData<Game.Vehicles.Odometer>(controller).m_Distance >= kFreshFromYardDistance)
			{
				return false;
			}

			if (!entityManager.HasBuffer<Game.Vehicles.LayoutElement>(controller))
			{
				return IsVehicleEmpty(entityManager, controller);
			}

			DynamicBuffer<Game.Vehicles.LayoutElement> layout = entityManager.GetBuffer<Game.Vehicles.LayoutElement>(controller, true);
			if (layout.Length == 0)
			{
				return IsVehicleEmpty(entityManager, controller);
			}

			for (int i = 0; i < layout.Length; i++)
			{
				if (!IsVehicleEmpty(entityManager, layout[i].m_Vehicle))
				{
					return false;
				}
			}

			return true;
		}

		private static bool IsVehicleEmpty(EntityManager entityManager, Entity vehicle)
		{
			if (entityManager.HasBuffer<Game.Vehicles.Passenger>(vehicle)
				&& entityManager.GetBuffer<Game.Vehicles.Passenger>(vehicle, true).Length > 0)
			{
				return false;
			}

			if (entityManager.HasBuffer<Game.Economy.Resources>(vehicle))
			{
				DynamicBuffer<Game.Economy.Resources> resources = entityManager.GetBuffer<Game.Economy.Resources>(vehicle, true);
				for (int i = 0; i < resources.Length; i++)
				{
					if (resources[i].m_Amount > 0)
					{
						return false;
					}
				}
			}

			return true;
		}
	}
}

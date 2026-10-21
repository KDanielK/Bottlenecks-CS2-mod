using System;
using Game.Pathfind;
using Unity.Entities;

namespace Bottlenecks.Systems
{
	// Identifies one finished route search result on a storage entity. A storage can finish a new search between
	// two updates without ever being seen as Pending, so "already handled this entity" is not enough: the result
	// itself (origin, destination, cost, distance, duration) is compared instead. Editing the path elements does not
	// change these values, so an edited route is not handled twice.
	internal struct RouteResultKey : IEquatable<RouteResultKey>
	{
		private Entity m_Origin;
		private Entity m_Destination;
		private float m_TotalCost;
		private float m_Distance;
		private float m_Duration;

		public static RouteResultKey Of(PathInformation info)
		{
			return new RouteResultKey
			{
				m_Origin = info.m_Origin,
				m_Destination = info.m_Destination,
				m_TotalCost = info.m_TotalCost,
				m_Distance = info.m_Distance,
				m_Duration = info.m_Duration,
			};
		}

		public bool Equals(RouteResultKey other)
		{
			return m_Origin == other.m_Origin && m_Destination == other.m_Destination && m_TotalCost == other.m_TotalCost &&
				m_Distance == other.m_Distance && m_Duration == other.m_Duration;
		}

		public override bool Equals(object obj)
		{
			return obj is RouteResultKey other && Equals(other);
		}

		public override int GetHashCode()
		{
			return m_Origin.GetHashCode() ^ (m_Destination.GetHashCode() * 31) ^ m_TotalCost.GetHashCode();
		}
	}
}

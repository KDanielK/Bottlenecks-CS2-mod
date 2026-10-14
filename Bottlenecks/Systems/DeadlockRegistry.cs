using System.Collections.Generic;
using Unity.Entities;

namespace Bottlenecks.Systems
{
	public static class DeadlockRegistry
	{
		public static readonly HashSet<Entity> Held = new HashSet<Entity>();

		public static void Clear()
		{
			Held.Clear();
		}
	}
}

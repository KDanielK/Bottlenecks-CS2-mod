using System.Collections.Generic;
using Unity.Entities;

namespace Bottlenecks.Systems
{
	public static class DeadlockRegistry
	{
		public static readonly HashSet<Entity> Held = new HashSet<Entity>();

		// Held trains that currently look free, and the frame since which they have been free. A train is
		// only released after staying free for a while, so a deadlock that briefly loosens and re-forms
		// does not release and re-announce its trains.
		public static readonly Dictionary<Entity, uint> FreeSince = new Dictionary<Entity, uint>();

		public static void Clear()
		{
			Held.Clear();
			FreeSince.Clear();
		}
	}
}

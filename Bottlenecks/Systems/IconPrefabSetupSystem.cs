using Game;
using UnityEngine.Scripting;

namespace Bottlenecks.Systems
{
	// Creates our notification icon prefabs. Registered in the PrefabUpdate phase before
	// PrefabInitializeSystem so the new prefabs are initialized in the same frame.
	public partial class IconPrefabSetupSystem : GameSystemBase
	{
		[Preserve]
		protected override void OnUpdate()
		{
			if (BottleneckIconPrefabs.CreatePrefabs(World))
			{
				Enabled = false;
			}
		}
	}
}

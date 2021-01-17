using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace BuildingOverhaul
{
	[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
	public class BuildingMessage
	{
		private BlockPos _position;
		private byte _face;
		private Vec3d _hitPosition;

		public BlockSelection Selection => new BlockSelection {
			Position    = _position,
			Face        = BlockFacing.ALLFACES[_face],
			HitPosition = _hitPosition,
		};

		private BuildingMessage() {  }

		public BuildingMessage(BlockSelection selection)
		{
			_position    = selection.Position;
			_face        = (byte)selection.Face.Index;
			_hitPosition = selection.HitPosition;
		}
	}
}

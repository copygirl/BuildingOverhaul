using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace BuildingOverhaul.Network
{
	[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
	public class BuildingMessage
	{
		private BlockPos _position;
		private byte _face;
		private Vec3d _hitPosition;
		private bool _didOffset;

		public string Shape { get; }

		public BlockSelection Selection => new BlockSelection {
			Position    = _position,
			Face        = BlockFacing.ALLFACES[_face],
			HitPosition = _hitPosition,
			DidOffset   = _didOffset,
		};

		// This is used by ProtoBuf, so ignore non-nullable warnings.
		#pragma warning disable CS8618
		private BuildingMessage() {  }
		#pragma warning restore

		public BuildingMessage(BlockSelection selection, string shape)
		{
			_position    = selection.Position;
			_face        = (byte)selection.Face.Index;
			_hitPosition = selection.HitPosition;
			_didOffset   = selection.DidOffset;
			Shape        = shape;
		}
	}
}

using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

[assembly: ModInfo("BuildingOverhaul",
	Description = "",
	Website = "",
	Authors = new []{ "copygirl" })]

namespace BuildingOverhaul
{
	public class BuildingOverhaulSystem : ModSystem
	{
		public const string MOD_ID = "buildingoverhaul";

		public const string FAILURE_NO_HAMMER    = MOD_ID + "-nohammer";
		public const string FAILURE_NO_RECIPE    = MOD_ID + "-norecipe";
		public const string FAILURE_NO_MATERIALS = MOD_ID + "-nomaterials";

		// Client
		public ICoreClientAPI ClientAPI { get; private set; }
		public IClientNetworkChannel ClientChannel { get; private set; }

		// Server
		public ICoreServerAPI ServerAPI { get; private set; }
		public IServerNetworkChannel ServerChannel { get; private set; }

		public override void StartClientSide(ICoreClientAPI api)
		{
			ClientAPI = api;
			api.Input.InWorldAction += OnAction;

			ClientChannel = api.Network.RegisterChannel(MOD_ID);
			ClientChannel.RegisterMessageType<BuildingMessage>();
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			ServerChannel = api.Network.RegisterChannel(MOD_ID);
			ServerChannel.RegisterMessageType<BuildingMessage>();
			ServerChannel.SetMessageHandler<BuildingMessage>(OnBuildingMessage);
		}

		private void OnAction(EnumEntityAction action, bool on,
		                      ref EnumHandling handled)
		{
			if ((action != EnumEntityAction.RightMouseDown) || !on) return;
			var player    = ClientAPI.World?.Player;
			var selection = player?.CurrentBlockSelection;
			if (selection == null) return;

			var result = TryBuild(player, selection, true);
			if (result.IsSuccess)
				ClientChannel.SendPacket(new BuildingMessage(selection));
			else if ((result.FailureCode != FAILURE_NO_HAMMER) && (result.FailureCode != FAILURE_NO_RECIPE))
				ClientAPI.TriggerIngameError(this, result.FailureCode,
					Lang.Get("placefailure-" + result.FailureCode, result.LangParams));
		}

		private void OnBuildingMessage(IServerPlayer player, BuildingMessage message)
		{
			var result = TryBuild(player, player.CurrentBlockSelection, false);
			if (!result.IsSuccess) {
				player.SendIngameError(result.FailureCode, null, result.LangParams);
				player.Entity.World.BlockAccessor.MarkBlockDirty(message.Selection.Position);
			}
		}

		private BuildResult TryBuild(IPlayer player, BlockSelection selection/*, shape? */, bool doOffset)
		{

			var inventory = player.InventoryManager;
			var offhandItem = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName)[10].Itemstack;
			if (!(offhandItem.Item is ItemHammer))
				return new BuildResult { FailureCode = FAILURE_NO_HAMMER };
			var hotbarItem = inventory.ActiveHotbarSlot.Itemstack;
			if ((hotbarItem == null) || !hotbarItem.Collectible.Code.BeginsWith("game", "plank-"))
				return new BuildResult { FailureCode = FAILURE_NO_RECIPE };

			var world    = player.Entity.World;
			var newBlock = world.GetBlock(new AssetLocation("game", $"planks-{hotbarItem.Collectible.LastCodePart()}"));

			if (doOffset) {
				var clickedBlock = world.BlockAccessor.GetBlock(selection.Position);
				if (!clickedBlock.IsReplacableBy(newBlock)) {
					selection.Position.Add(selection.Face);
					selection.DidOffset = true;
				}
			}

			var failureCode = "__ignore__";
			return newBlock.TryPlaceBlock(world, player, new ItemStack(newBlock), selection, ref failureCode)
				? new BuildResult() : new BuildResult { FailureCode = failureCode };

		}

		private class BuildResult
		{
			public string FailureCode  = "__ignore__";
			public object[] LangParams = new object[0];

			public bool IsSuccess => (FailureCode == "__ignore__");
		}
	}
}

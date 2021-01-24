using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

[assembly: ModInfo("BuildingOverhaul",
	Description = "Allows building using materials instead of having to craft and carry individual building blocks in your inventory",
	Website = "https://github.com/copygirl/BuildingOverhaul",
	Authors = new[] { "copygirl" })]

namespace BuildingOverhaul
{
	public partial class BuildingOverhaulSystem : ModSystem
	{
		public const string MOD_ID = "buildingoverhaul";

		/// <summary> Failure code when no matching recipe was found for the held items. </summary>
		public const string FAILURE_NO_RECIPE = MOD_ID + ":norecipe";
		/// <summary> Failure code when no matching recipe was found for the selected shape. </summary>
		public const string FAILURE_NO_SHAPE = MOD_ID + ":noshape";
		/// <summary> Failure code when the required materials aren't available in-inventory. </summary>
		public const string FAILURE_NO_MATERIALS = MOD_ID + ":nomaterials";


		// == Common properties ==

		/// <summary> Statically available instance of the API for resolving purposes. </summary>
		public static ICoreAPI API { get; private set; }

		/// <summary> List of recipes grouped by which tools they share, such as "game:hammer-*". </summary>
		public BuildingRecipes Recipes { get; } = new();

		// == Client properties ==

		/// <summary> Statically available instance of the system, used by
		///           Harmony patch to call <see cref="OnInWorldInteract"/>. </summary>
		public static BuildingOverhaulSystem Instance { get; private set; }

		public ICoreClientAPI ClientAPI { get; private set; }
		public IClientNetworkChannel ClientChannel { get; private set; }
		public GuiDialogShapeSelector Dialog { get; private set; }
		public Harmony Harmony { get; } = new(MOD_ID);

		// == Server properties ==

		public ICoreServerAPI ServerAPI { get; private set; }
		public IServerNetworkChannel ServerChannel { get; private set; }

		public override void StartClientSide(ICoreClientAPI api)
		{
			Instance = this;
			API = ClientAPI = api;

			ClientChannel = api.Network.RegisterChannel(MOD_ID)
				.RegisterMessageType<BuildingMessage>()
				.RegisterMessageType<BuildingRecipes.Message>()
				.SetMessageHandler<BuildingRecipes.Message>(Recipes.LoadFromMessage);

			Dialog = new GuiDialogShapeSelector(api, Recipes);
			api.Gui.RegisterDialog(Dialog);

			// We're using the IsPlayerReady event because it appears
			// hotkeys are registered after StartClientSide is called?
			api.Event.IsPlayerReady += (ref EnumHandling handling)
				=> { HookToolModeSelectHotkey(); return true; };

			Harmony.PatchAll();
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			API = ServerAPI = api;

			ServerChannel = api.Network.RegisterChannel(MOD_ID)
				.RegisterMessageType<BuildingMessage>()
				.RegisterMessageType<BuildingRecipes.Message>()
				.SetMessageHandler<BuildingMessage>(OnBuildingMessage);

			api.Event.SaveGameLoaded += () => Recipes.LoadFromAssets(ServerAPI.Assets, Mod.Logger);
			api.Event.PlayerJoin += player => ServerChannel.SendPacket(Recipes.CachedMessage, player);
		}

		public override void Dispose()
		{
			API = null;
			if (ClientAPI != null) {
				Instance = null;
				Harmony.UnpatchAll(MOD_ID);
			}
		}


		/// <summary>
		/// Hooks into the tool mode selection hotkey and instead shows the selection
		/// dialog if a recipe is found for the held items, or closes if already opened.
		/// </summary>
		private void HookToolModeSelectHotkey()
		{
			var hotkey = ClientAPI.Input.HotKeys["toolmodeselect"];
			var originalHandler = hotkey.Handler;
			hotkey.Handler = (keyCombination) => !Dialog.IsOpened()
				? Dialog.TryOpen() || originalHandler(keyCombination)
				: Dialog.TryClose();
		}


		/// <summary>
		/// Called by the Harmony patch, because <see cref="IInputAPI.InWorldAction"/>
		/// is useless for most usecases. Attempts to find a recipe and place its
		/// output. If successful, sends a message to the server to do the same.
		/// </summary>
		public bool OnInWorldInteract()
		{
			var player    = ClientAPI.World.Player;
			var selection = player.CurrentBlockSelection.Clone();

			var result = TryBuild(player, selection, Dialog.CurrentShape, true);
			if (result.IsSuccess) {
				ClientChannel.SendPacket(new BuildingMessage(selection, Dialog.CurrentShape));
				TriggerNeighbourBlocksUpdate(ClientAPI.World, selection.Position);
			} else {
				// Ignore missing recipe failures, this just means we aren't holding the right items.
				if ((result.FailureCode == FAILURE_NO_RECIPE)) return false;
				// But otherwise do show an error message.
				ClientAPI.TriggerIngameError(this, result.FailureCode,
					Lang.Get(result.FailureCode, result.LangParams));
			}

			// Returning true will cause the Harmony patch to not continue the default
			// behavior, preventing block interaction, item usage and block placement.
			return true;
		}

		/// <summary>
		/// Called when a player sends a <see cref="BuildingMessage"/> to the server.
		/// Attempts to find a recipe and place its output at the player's desired selection.
		/// </summary>
		private void OnBuildingMessage(IServerPlayer player, BuildingMessage message)
		{
			var world  = player.Entity.World;
			var result = TryBuild(player, message.Selection, message.Shape, false);
			if (result.IsSuccess) TriggerNeighbourBlocksUpdate(world, message.Selection.Position);
			else world.BlockAccessor.MarkBlockDirty(message.Selection.Position);
		}

		private static void TriggerNeighbourBlocksUpdate(IWorldAccessor world, BlockPos pos)
		{
			foreach (var facing in BlockFacing.ALLFACES) {
				var position = pos.AddCopy(facing);
				world.BlockAccessor.GetBlock(position).OnNeighbourBlockChange(world, position, pos);
			}
		}


		/// <summary>
		/// Called when attempting to build using the building overhaul system.
		/// Will try to place the output block of a recipe matching the held items.
		/// </summary>
		/// <param name="selection">
		/// On client, represents the block the player is aiming at. (Selection will be offset if necessary.)
		/// On server, represents the block where the player wants to build. (Selection has been pre-offset.)
		/// </param>
		/// <param name="doOffset"> Whether to offset selection if clicking a non-replacable block. True on client. </param>
		private BuildResult TryBuild(IPlayer player, BlockSelection selection, string shape, bool doOffset)
		{
			var inventory   = player.InventoryManager;
			var offhandItem = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName)[10].Itemstack;
			var hotbarItem  = inventory.ActiveHotbarSlot.Itemstack;

			var matches = Recipes.Find(offhandItem, hotbarItem);
			if (matches.Count == 0) return new(FAILURE_NO_RECIPE);

			var match = matches.Find(match => match.Recipe.Shape == shape);
			if (match == null) return new(FAILURE_NO_SHAPE, shape, hotbarItem.GetName());

			var world = player.Entity.World;
			var block = match.Output.Block;

			if (doOffset) {
				var clickedBlock = world.BlockAccessor.GetBlock(selection.Position);
				if (!clickedBlock.IsReplacableBy(block)) {
					selection.Position.Offset(selection.Face);
					selection.DidOffset = true;
				}
			}

			var failureCode = "__ignore__";
			return block.TryPlaceBlock(world, player, match.Output, selection, ref failureCode)
				? new() : new("placefailure-" + failureCode);
		}

		/// <summary>
		/// A result representing either success or failure. When failed, contains
		/// failure code and language parameters, used to display / send error messages.
		/// </summary>
		private class BuildResult
		{
			public string FailureCode { get; }
			public object[] LangParams { get; }
			public bool IsSuccess => (FailureCode == "__ignore__");
			public BuildResult(string failureCode = "__ignore__", params object[] langParams)
				{ FailureCode = failureCode; LangParams = langParams; }
		}
	}
}

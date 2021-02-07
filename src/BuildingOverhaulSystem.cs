using BuildingOverhaul.Client;
using BuildingOverhaul.Network;
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


		/// <summary> Statically available instance of the API for resolving purposes. </summary>
		public static ICoreAPI API { get; private set; } = null!;

		/// <summary> List of recipes grouped by which tools they share, such as "game:hammer-*". </summary>
		public BuildingRecipes Recipes { get; } = new();

		public Harmony Harmony { get; } = new(MOD_ID);


		public override void StartClientSide(ICoreClientAPI api)
		{
			API = api;
			Harmony.PatchAll();

			var channel = api.Network.RegisterChannel(MOD_ID)
				.RegisterMessageType<BuildingMessage>()
				.RegisterMessageType<BuildingRecipes.Message>()
				.SetMessageHandler<BuildingRecipes.Message>(Recipes.LoadFromMessage);

			var selection = new RecipeSelectionHandler(api, Recipes);
			var dialog    = new GuiDialogShapeSelector(api, Recipes, selection);

			SystemMouseInWorldInteractions_HandleMouseInteractionsBlockSelected_Patch.InWorldInteract = ()
				=> OnInWorldInteract(api, channel, selection.CurrentShape);

			// We're using the IsPlayerReady event because it appears
			// hotkeys are registered after StartClientSide is called?
			api.Event.IsPlayerReady += (ref EnumHandling handling)
				=> { HookToolModeSelectHotkey(api, dialog); return true; };
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			API = api;

			var channel = api.Network.RegisterChannel(MOD_ID)
				.RegisterMessageType<BuildingMessage>()
				.RegisterMessageType<BuildingRecipes.Message>()
				.SetMessageHandler<BuildingMessage>(OnBuildingMessage);

			api.Event.SaveGameLoaded += () => Recipes.LoadFromAssets(api.Assets, Mod.Logger);
			api.Event.PlayerJoin += player => channel.SendPacket(Recipes.CachedMessage, player);
		}

		public override void Dispose()
		{
			if (API is ICoreClientAPI)
				Harmony.UnpatchAll(MOD_ID);
			API = null!;
		}


		/// <summary>
		/// Hooks into the tool mode selection hotkey and instead shows the selection
		/// dialog if a recipe is found for the held items, or closes if already opened.
		/// </summary>
		private void HookToolModeSelectHotkey(ICoreClientAPI api, GuiDialog dialog)
		{
			var hotkey = api.Input.HotKeys["toolmodeselect"];
			var originalHandler = hotkey.Handler;
			hotkey.Handler = (keyCombination) => !dialog.IsOpened()
				? dialog.TryOpen() || originalHandler(keyCombination)
				: dialog.TryClose();
		}


		/// <summary>
		/// Called by the Harmony patch, because <see cref="IInputAPI.InWorldAction"/>
		/// is useless for most usecases. Attempts to find a recipe and place its
		/// output. If successful, sends a message to the server to do the same.
		/// </summary>
		public bool OnInWorldInteract(ICoreClientAPI api,
			IClientNetworkChannel channel, string currentShape)
		{
			var player    = api.World.Player;
			var selection = player.CurrentBlockSelection.Clone();

			var result = TryBuild(player, selection, currentShape, true);
			if (result.IsSuccess) {
				channel.SendPacket(new BuildingMessage(selection, currentShape));
				TriggerNeighbourBlocksUpdate(api.World, selection.Position);
			} else {
				// Ignore missing recipe failures, this just means we aren't holding the right items.
				if ((result.FailureCode == FAILURE_NO_RECIPE)) return false;
				// But otherwise do show an error message.
				api.TriggerIngameError(this, result.FailureCode,
					Lang.Get(result.FailureCode, result.LangParams));
			}

			// Returning true will cause the Harmony patch to not continue the
			// default behavior, preventing item usage and block placement.
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
			if (result.IsSuccess)
				TriggerNeighbourBlocksUpdate(world, message.Selection.Position);
			else {
				// These methods unfortunately send the block and player data to all nearby
				// players rather than just the original one, but we'll just live with that.
				world.BlockAccessor.MarkBlockDirty(message.Selection.Position);
				player.BroadcastPlayerData(true);
			}
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
			var offhandItem = player.Entity.LeftHandItemSlot.Itemstack;
			var hotbarItem  = player.Entity.RightHandItemSlot.Itemstack;

			var matches = Recipes.Find(offhandItem, hotbarItem);
			if (matches.Count == 0) return new(FAILURE_NO_RECIPE);

			var match = matches.Find(match => match.Recipe.Shape == shape);
			if (match == null) return new(FAILURE_NO_SHAPE, shape, hotbarItem.GetName());

			System.Action? applyBuildingCost = null;
			if (player.WorldData.CurrentGameMode != EnumGameMode.Creative) {
				// If not in creative mode, test to see if the required materials are available.
				applyBuildingCost = Recipes.FindIngredients(player, match);
				if (applyBuildingCost == null) return new(FAILURE_NO_MATERIALS, match.Output.GetName());
			}

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
			if (!block.TryPlaceBlock(world, player, match.Output, selection, ref failureCode))
				return new("placefailure-" + failureCode);

			// Actually take the required ingredients out of
			// the player's inventory (if not in creative mode).
			applyBuildingCost?.Invoke();
			return new();
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

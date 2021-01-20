using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

[assembly: ModInfo("BuildingOverhaul",
	Description = "",
	Website = "",
	Authors = new[] { "copygirl" })]

namespace BuildingOverhaul
{
	public partial class BuildingOverhaulSystem : ModSystem
	{
		public const string MOD_ID = "buildingoverhaul";

		/// <summary> Failure code when no matching recipe was found for the held items. </summary>
		public const string FAILURE_NO_RECIPE = MOD_ID + "-norecipe";
		/// <summary> Failure code when the required materials aren't available in-inventory. </summary>
		public const string FAILURE_NO_MATERIALS = MOD_ID + "-nomaterials";


		// == Common properties ==

		/// <summary> List of recipes grouped by which tools they share, such as "game:hammer-*". </summary>
		public BuildingRecipes Recipes { get; } = new();

		// == Client properties ==

		/// <summary> Statically available instance of the system, used by
		///           Harmony patch to call <see cref="OnInWorldInteract"/>. </summary>
		public static BuildingOverhaulSystem Instance { get; private set; }

		public ICoreClientAPI ClientAPI { get; private set; }
		public IClientNetworkChannel ClientChannel { get; private set; }
		public Harmony Harmony { get; } = new(MOD_ID);

		// == Server properties ==

		public ICoreServerAPI ServerAPI { get; private set; }
		public IServerNetworkChannel ServerChannel { get; private set; }


		public override void StartClientSide(ICoreClientAPI api)
		{
			Instance  = this;
			ClientAPI = api;

			ClientChannel = api.Network.RegisterChannel(MOD_ID)
				.RegisterMessageType<BuildingMessage>()
				.RegisterMessageType<BuildingRecipes.Message>()
				.SetMessageHandler<BuildingRecipes.Message>(Recipes.LoadFromMessage);

			Harmony.PatchAll();
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			ServerAPI = api;

			ServerChannel = api.Network.RegisterChannel(MOD_ID)
				.RegisterMessageType<BuildingMessage>()
				.RegisterMessageType<BuildingRecipes.Message>()
				.SetMessageHandler<BuildingMessage>(OnBuildingMessage);

			api.Event.SaveGameLoaded += () => Recipes.LoadFromAssets(ServerAPI.Assets, Mod.Logger);
			api.Event.PlayerJoin += player => ServerChannel.SendPacket(Recipes.CachedMessage, player);
		}

		public override void Dispose()
		{
			// On client, undo the Harmony patch.
			if (ClientAPI != null) {
				Instance = null;
				Harmony.UnpatchAll(MOD_ID);
			}
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

			var result = TryBuild(player, selection, true);
			if (result.IsSuccess)
				ClientChannel.SendPacket(new BuildingMessage(selection));
			else {
				// Ignore missing recipe failures, this just means we aren't holding the right items.
				if ((result.FailureCode == FAILURE_NO_RECIPE)) return false;
				// But otherwise do show an error message.
				ClientAPI.TriggerIngameError(this, result.FailureCode,
					Lang.Get("placefailure-" + result.FailureCode, result.LangParams));
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
			var result = TryBuild(player, message.Selection, false);
			if (!result.IsSuccess) {
				// TODO: This sends an "ingameerror-*" message rather than "placefailure-*". Duh!
				// player.SendIngameError(result.FailureCode, null, result.LangParams);
				player.Entity.World.BlockAccessor.MarkBlockDirty(message.Selection.Position);
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
		private BuildResult TryBuild(IPlayer player, BlockSelection selection/*, shape? */, bool doOffset)
		{
			var inventory   = player.InventoryManager;
			var offhandItem = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName)[10].Itemstack;
			var hotbarItem  = inventory.ActiveHotbarSlot.Itemstack;
			if (!Recipes.TryGet(offhandItem, hotbarItem, out var recipe, out var output))
				return new(){ FailureCode = FAILURE_NO_RECIPE };

			var world    = player.Entity.World;
			var newBlock = world.GetBlock(output);
			if (newBlock == null) {
				Mod.Logger.Warning("Could not find block '{0}' for recipe '{1}'", output, recipe.Location);
				return new(){ FailureCode = FAILURE_NO_RECIPE };
			}

			if (doOffset) {
				var clickedBlock = world.BlockAccessor.GetBlock(selection.Position);
				if (!clickedBlock.IsReplacableBy(newBlock)) {
					selection.Position.Offset(selection.Face);
					selection.DidOffset = true;
				}
			}

			var failureCode = "__ignore__";
			return newBlock.TryPlaceBlock(world, player, new(newBlock), selection, ref failureCode)
				? new() : new(){ FailureCode = failureCode };
		}

		/// <summary>
		/// A result representing either success or failure. When failed, contains
		/// failure code and language parameters, used to display / send error messages.
		/// </summary>
		private class BuildResult
		{
			public string FailureCode  = "__ignore__";
			public object[] LangParams = new object[0];

			public bool IsSuccess => (FailureCode == "__ignore__");
		}
	}
}

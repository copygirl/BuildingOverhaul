using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Vintagestory.API.Util;
using Vintagestory.Client.NoObf;

[assembly: ModInfo("BuildingOverhaul",
	Description = "",
	Website = "",
	Authors = new []{ "copygirl" })]

namespace BuildingOverhaul
{
	public class BuildingOverhaulSystem : ModSystem
	{
		public const string MOD_ID = "buildingoverhaul";

		/// <summary> Failure code when no matching recipe was found for the held items. </summary>
		public const string FAILURE_NO_RECIPE = MOD_ID + "-norecipe";
		/// <summary> Failure code when the required materials aren't available in-inventory. </summary>
		public const string FAILURE_NO_MATERIALS = MOD_ID + "-nomaterials";


		// ============
		// == COMMON ==
		// ============

		/// <summary> List of recipes grouped by which tools they share, such as "game:hammer-*". </summary>
		public List<List<BuildingRecipe>> RecipesByTool { get; set; }

		/// <summary>
		/// Called when attempting to build using the building overhaul system.
		/// Will search for and check for valid recipes and try to place the output block of the matched recipe.
		/// </summary>
		/// <param name="selection">
		/// On client, represents the block the player is aiming at. (Selection will be offset if necessary.)
		/// On server, represents the block where the player wants to build. (Selection has been pre-offset.)
		/// </param>
		/// <param name="doOffset"> Whether to offset selection if clicking a non-replacable block. True on client. </param>
		private BuildResult TryBuild(IPlayer player, BlockSelection selection/*, shape? */, bool doOffset)
		{
			var inventory = player.InventoryManager;

			// Make sure the player is holding a valid tool in their offhand slot.
			var offhandItem = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName)[10].Itemstack;
			var recipes = RecipesByTool.Find(list => list[0].Tool.Matches(offhandItem));
			if (recipes == null) return new(){ FailureCode = FAILURE_NO_RECIPE };

			// Make sure there is a recipe that matches the material
			// the player is holding in their active hotbar slot.
			var hotbarItem = inventory.ActiveHotbarSlot.Itemstack;
			if (hotbarItem == null) return new(){ FailureCode = FAILURE_NO_RECIPE };
			var recipe = recipes.Find(r => r.Material.Matches(hotbarItem));
			if (recipe == null) return new(){ FailureCode = FAILURE_NO_RECIPE };

			// Collect mappings for Tool and Material, and extract them from the held items.
			// For example for a recipe with `{ "code": "game:plank-*", "name": "wood" }`,
			// when holding "game:plank-acacia", this will create a "wood" => "acacia" mapping.
			var mappings = new Dictionary<string, string>();
			void AddNameToCodeMapping(Ingredient wildcard, ItemStack stack)
			{
				if (wildcard.Name == null) return;
				var recipePath = wildcard.Code.Path;
				var actualPath = stack.Collectible.Code.Path;
				var wildcardIndex = recipePath.IndexOf('*');
				var value = actualPath.Substring(wildcardIndex, actualPath.Length - recipePath.Length + 1);
				mappings.Add(wildcard.Name, value);
			}
			AddNameToCodeMapping(recipe.Tool, offhandItem);
			AddNameToCodeMapping(recipe.Material, hotbarItem);

			// Apply the mappings so a "game:plank-{wood}" will for example turn into "game:plank-acacia".
			// TODO: Support wildcards in output?
			var output = recipe.Output.Clone();
			foreach (var mapping in mappings)
				output.Path = output.Path.Replace("{" + mapping.Key + "}", mapping.Value);

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


		// ============
		// == CLIENT ==
		// ============

		/// <summary> Statically available instance of the system, used by
		///           Harmony patch to call <see cref="OnInWorldInteract"/>. </summary>
		public static BuildingOverhaulSystem Instance { get; private set; }

		public ICoreClientAPI ClientAPI { get; private set; }
		public IClientNetworkChannel ClientChannel { get; private set; }
		public Harmony Harmony { get; private set; }

		public override void StartClientSide(ICoreClientAPI api)
		{
			Instance  = this;
			ClientAPI = api;

			ClientChannel = api.Network.RegisterChannel(MOD_ID)
				.RegisterMessageType<BuildingMessage>()
				.RegisterMessageType<RecipesMessage>()
				.SetMessageHandler<RecipesMessage>(OnRecipesMessage);

			Harmony = new(MOD_ID);
			Harmony.PatchAll();
		}

		public override void Dispose()
		{
			// On client, undo the Harmony patch.
			if (ClientAPI != null) {
				Instance = null;
				Harmony.UnpatchAll(MOD_ID);
			}
		}

		private void OnRecipesMessage(RecipesMessage message)
			=> RecipesByTool = message.UnpackRecipes();

		public bool OnInWorldInteract()
		{
			var player    = ClientAPI.World?.Player;
			var selection = player?.CurrentBlockSelection?.Clone();
			if (selection == null) return false;

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

		[HarmonyPatch(typeof(SystemMouseInWorldInteractions), "HandleMouseInteractionsBlockSelected")]
		class SystemMouseInWorldInteractions_HandleMouseInteractionsBlockSelected_Patch
		{
			static IEnumerable<CodeInstruction> Transpiler(
				IEnumerable<CodeInstruction> instructions,
				ILGenerator generator)
			{
				var enumerator = instructions.GetEnumerator();

				// Yield instructions until this specific one:
				//   var handling = EnumHandling.PassThrough;
				var HANDLING_INDEX = 7; // Index of the handling method local.
				while (enumerator.MoveNext()) {
					yield return enumerator.Current;
					if ((enumerator.Current.opcode == OpCodes.Stloc_S) &&
					    (enumerator.Current.operand is LocalBuilder local) &&
					    (local.LocalIndex == HANDLING_INDEX)) break;
				}

				// Insert call to BuildingOverhaulSystem.Instance.OnInWorldInteract.
				// If OnInWorldInteract returns true, return from the method immediately.
				var falseLabel = generator.DefineLabel();
				yield return new(OpCodes.Call, typeof(BuildingOverhaulSystem).GetProperty(nameof(BuildingOverhaulSystem.Instance)).GetMethod);
				yield return new(OpCodes.Callvirt, typeof(BuildingOverhaulSystem).GetMethod(nameof(BuildingOverhaulSystem.OnInWorldInteract)));
				yield return new(OpCodes.Brfalse_S, falseLabel);
				yield return new(OpCodes.Ret);

				// Replace the following instruction so we can specify the label to jump to.
				enumerator.MoveNext();
				yield return new(enumerator.Current){ labels = new(){ falseLabel } };

				// Yield the rest of the instructions.
				while (enumerator.MoveNext())
					yield return enumerator.Current;
			}
		}


		// ============
		// == SERVER ==
		// ============

		public ICoreServerAPI ServerAPI { get; private set; }
		public IServerNetworkChannel ServerChannel { get; private set; }
		public RecipesMessage RecipesMessage { get; private set; }

		public override void StartServerSide(ICoreServerAPI api)
		{
			ServerAPI = api;

			ServerChannel = api.Network.RegisterChannel(MOD_ID)
				.RegisterMessageType<BuildingMessage>()
				.RegisterMessageType<RecipesMessage>()
				.SetMessageHandler<BuildingMessage>(OnBuildingMessage);

			api.Event.SaveGameLoaded += OnSaveGameLoaded;
			api.Event.PlayerJoin += OnPlayerJoin;
		}

		public void OnSaveGameLoaded()
		{
			var assets  = ServerAPI.Assets.GetMany<JToken>(Mod.Logger, "recipes/" + MOD_ID);
			var recipes = new List<BuildingRecipe>();

			void LoadRecipe(AssetLocation location, JToken token)
			{
				var recipe = token.ToObject<BuildingRecipe>(location.Domain);
				// TODO: Do some validation to make it easier to spot errors?
				recipe.Location = location;
				// Ensure that every ingredient's AllowedVariants is sorted and distinct.
				// This is to make sure the array can be easily tested for equality.
				foreach (var ingredient in new []{ recipe.Tool, recipe.Material }.Append(recipe.Ingredients))
					ingredient.AllowedVariants = ingredient.AllowedVariants?.OrderBy(x => x)?.Distinct()?.ToArray();
				recipes.Add(recipe);
			}

			foreach (var asset in assets)
			switch (asset.Value) {
				case JObject obj: LoadRecipe(asset.Key, obj); break;
				case JArray arr: arr.Do(token => LoadRecipe(asset.Key, token)); break;
			}

			var recipesCount = 0;
			RecipesByTool = new();
			foreach (var recipe in recipes) {
				if (!recipe.Enabled) continue;
				var groupedRecipes = RecipesByTool.Find(list => (recipe.Tool == list[0].Tool));
				if (groupedRecipes == null) RecipesByTool.Add(groupedRecipes = new());
				groupedRecipes.Add(recipe);
				recipesCount++;
			}

			// Cache RecipesMessage to be sent to clients on join.
			RecipesMessage = new RecipesMessage(RecipesByTool);

			Mod.Logger.Event("{0} building recipes loaded", recipesCount);
		}

		private void OnPlayerJoin(IServerPlayer player)
			=> ServerChannel.SendPacket(RecipesMessage, player);

		private void OnBuildingMessage(IServerPlayer player, BuildingMessage message)
		{
			var result = TryBuild(player, message.Selection, false);
			if (!result.IsSuccess) {
				player.SendIngameError(result.FailureCode, null, result.LangParams);
				player.Entity.World.BlockAccessor.MarkBlockDirty(message.Selection.Position);
			}
		}
	}
}

using System;
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

		public const string FAILURE_NO_RECIPE    = MOD_ID + "-norecipe";
		public const string FAILURE_NO_MATERIALS = MOD_ID + "-nomaterials";

		// FIXME: This won't work in multiplayer - need to send recipes to clients.
		public static List<List<BuildingRecipe>> RecipesByTool { get; set; }

		// Client
		public static BuildingOverhaulSystem Instance { get; private set; }
		public ICoreClientAPI ClientAPI { get; private set; }
		public IClientNetworkChannel ClientChannel { get; private set; }
		public Harmony Harmony { get; private set; }

		// Server
		public ICoreServerAPI ServerAPI { get; private set; }
		public IServerNetworkChannel ServerChannel { get; private set; }

		public override void StartClientSide(ICoreClientAPI api)
		{
			Instance  = this;
			ClientAPI = api;

			ClientChannel = api.Network.RegisterChannel(MOD_ID);
			ClientChannel.RegisterMessageType<BuildingMessage>();

			Harmony = new(MOD_ID);
			Harmony.PatchAll();
		}

		public override void StartServerSide(ICoreServerAPI api)
		{
			ServerAPI = api;

			ServerChannel = api.Network.RegisterChannel(MOD_ID);
			ServerChannel.RegisterMessageType<BuildingMessage>();
			ServerChannel.SetMessageHandler<BuildingMessage>(OnBuildingMessage);

			api.Event.SaveGameLoaded += OnSaveGameLoaded;
		}

		public override void Dispose()
		{
			if (ClientAPI != null) {
				Instance = null;
				Harmony.UnpatchAll(MOD_ID);
			}
		}

		public void OnSaveGameLoaded()
		{
			var assets  = ServerAPI.Assets.GetMany<JToken>(Mod.Logger, "recipes/" + MOD_ID);
			var recipes = new List<BuildingRecipe>();

			void LoadRecipe(AssetLocation location, JToken token)
			{
				var recipe = token.ToObject<BuildingRecipe>(location.Domain);
				recipe.Location = location;
				// Ensure that every ingredient's AllowedVariants is distinct and sorted.
				foreach (var ingredient in new []{ recipe.Tool, recipe.Material }.Append(recipe.Ingredients))
					ingredient.AllowedVariants = ingredient.AllowedVariants?.OrderBy(x => x)?.Distinct()?.ToArray();
				recipes.Add(recipe);
			}

			foreach (var asset in assets)
			switch (asset.Value) {
				case JObject obj: LoadRecipe(asset.Key, obj); break;
				case JArray arr: arr.Do(token => LoadRecipe(asset.Key, token)); break;
				default: throw new Exception("Unexpected JToken type " + asset.Value.Type);
			}

			RecipesByTool = new();
			foreach (var recipe in recipes) {
				if (!recipe.Enabled) continue;
				var groupedRecipes = RecipesByTool.Find(list => (recipe.Tool == list[0].Tool));
				if (groupedRecipes == null) RecipesByTool.Add(groupedRecipes = new());
				groupedRecipes.Add(recipe);
			}

			Mod.Logger.Event("{0} building recipes loaded", recipes.Count);
		}

		public bool OnInWorldInteract()
		{
			var player    = ClientAPI.World?.Player;
			var selection = player?.CurrentBlockSelection?.Clone();
			if (selection == null) return false;

			var result = TryBuild(player, selection, true);
			if (!result.IsSuccess) {
				// Ignore missing recipe failures, this just means we aren't holding the right items.
				if ((result.FailureCode == FAILURE_NO_RECIPE)) return false;
				// But otherwise do show an error message.
				ClientAPI.TriggerIngameError(this, result.FailureCode,
					Lang.Get("placefailure-" + result.FailureCode, result.LangParams));
			} else ClientChannel.SendPacket(new BuildingMessage(selection));
			return true;
		}

		private void OnBuildingMessage(IServerPlayer player, BuildingMessage message)
		{
			var result = TryBuild(player, message.Selection, false);
			if (!result.IsSuccess) {
				player.SendIngameError(result.FailureCode, null, result.LangParams);
				player.Entity.World.BlockAccessor.MarkBlockDirty(message.Selection.Position);
			}
		}

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

			var codes = new Dictionary<string, string>();
			void AddNameToCodeMapping(Ingredient wildcard, ItemStack stack)
			{
				if (wildcard.Name == null) return;
				var recipePath = wildcard.Code.Path;
				var actualPath = stack.Collectible.Code.Path;
				var wildcardIndex = recipePath.IndexOf('*');
				var value = actualPath.Substring(wildcardIndex, actualPath.Length - recipePath.Length + 1);
				codes.Add(wildcard.Name, value);
			}
			AddNameToCodeMapping(recipe.Tool, offhandItem);
			AddNameToCodeMapping(recipe.Material, hotbarItem);

			// TODO: Support wildcards in output?
			var output = recipe.Output.Clone();
			foreach (var code in codes)
				output.Path = output.Path.Replace("{" + code.Key + "}", code.Value);

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

		private class BuildResult
		{
			public string FailureCode  = "__ignore__";
			public object[] LangParams = new object[0];

			public bool IsSuccess => (FailureCode == "__ignore__");
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
				var HANDLING_INDEX = 7; // Index of the handling method local variable.
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
	}
}

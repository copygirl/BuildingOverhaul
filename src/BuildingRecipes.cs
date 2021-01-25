using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using ProtoBuf;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Util;

namespace BuildingOverhaul
{
	public class BuildingRecipes
	{
		private List<List<Recipe>> _byTool;
		private Dictionary<Ingredient, List<CollectibleObject>> _ingredientLookup;

		/// <summary> Created once in <see cref="LoadFromAssets"/> to be sent to players when they join the server. </summary>
		public Message CachedMessage { get; private set; }

		[ProtoContract(ImplicitFields = ImplicitFields.AllFields)]
		public class Message { internal byte[] _data; }


		/// <summary> Finds all recipes matching the specified tool and material. </summary>
		/// <param name="tool"> The tool stack, typically an item held in the offhand slot. </param>
		/// <param name="material"> The material stack, typically an item held in the active hotbar slot. </param>
		/// <returns> An ordered dictionary (keyed by shape) containing all matched recipes, empty if none. </returns>
		public List<RecipeMatch> Find(ItemStack tool, ItemStack material)
		{
			var resolver = BuildingOverhaulSystem.API.World;
			var recipes  = _byTool.Find(list => list[0].Tool.Matches(tool));
			if ((recipes == null) || (material == null)) return new();

			var matches = new List<RecipeMatch>();
			foreach (var recipe in recipes) {
				if (!recipe.Material.Matches(material)) continue;
				var ingredients = new List<List<ItemStack>>(recipe.Ingredients.Length);
				foreach (var ingredient in recipe.Ingredients) {
					if (ingredient.Code.IsWildCard) {
						if (_ingredientLookup.TryGetValue(ingredient, out var collectibles))
							ingredients.Add(collectibles.Select(collectible =>
								new ItemStack(collectible, ingredient.Quantity)).ToList());
					} else {
						var ingredientLoc = ingredient.Code
							.ApplyMapping(recipe.Tool, tool)
							.ApplyMapping(recipe.Material, material);
						var collectible = (ingredient.Type == EnumItemClass.Block)
							? (CollectibleObject)resolver.GetBlock(ingredientLoc)
							: (CollectibleObject)resolver.GetItem(ingredientLoc);
						if (collectible == null) goto skip;
						ingredients.Add(new(){ new ItemStack(collectible, ingredient.Quantity) });
					}
				}

				var outputLoc = recipe.Output.Code
					.ApplyMapping(recipe.Tool, tool)
					.ApplyMapping(recipe.Material, material);
				var block = resolver.GetBlock(outputLoc);
				if (block == null) continue;

				var attributes = (TreeAttribute)recipe.Output.Attributes ?? new TreeAttribute();
				var output     = new ItemStack(block.Id, EnumItemClass.Block, 1, attributes, resolver);
				matches.Add(new RecipeMatch(recipe, ingredients, output));
				skip: {  }
			}
			return matches;
		}

		/// <summary>
		/// Attempts to find the ingredients for the specified matched recipe in the specified player inventory
		/// (backpack and hotbar). If all required ingredients are found, returns an action that can be invoked
		/// to take those ingredients out of the player's inventory and apply damage to the held tool.
		/// If they are not found, returns <c>null</c>.
		/// </summary>
		/// <param name="missing"> List which is filled to map ingredient to missing amount (by index). </param>
		public System.Action FindIngredients(IPlayer player, RecipeMatch match, List<int> missing = null)
		{
			var inventory = player.InventoryManager;
			var backpack  = inventory.GetOwnInventory(GlobalConstants.backpackInvClassName);
			var hotbar    = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName);
			var allSlots  = backpack.Concat(hotbar);

			System.Action applyBuildingCost = null;
			missing?.Clear(); var anyMissing = false;
			for (var i = 0; i < match.Recipe.Ingredients.Length; i++) {
				var ingredient = match.Recipe.Ingredients[i];
				var resolved   = match.Ingredients[i];
				var remaining  = ingredient.Quantity;
				foreach (var slot in allSlots) {
					if (!resolved.Any(stack => stack.Satisfies(slot?.Itemstack))) continue;
					var count = Math.Min(slot.Itemstack.StackSize, remaining);
					applyBuildingCost += () => {
						slot.TakeOut(count);
						slot.MarkDirty();
					};
					remaining -= count;
					if (remaining <= 0) break;
				}
				missing?.Add(remaining);
				if (remaining > 0) anyMissing = true;
			}
			if (anyMissing) return null;
			if (match.Recipe.ToolDurabilityCost > 0)
				applyBuildingCost += () => {
					var offhandSlot = player.Entity.LeftHandItemSlot;
					offhandSlot.Itemstack.Item.DamageItem(player.Entity.World,
						player.Entity, offhandSlot, match.Recipe.ToolDurabilityCost);
				};
			return applyBuildingCost;
		}


		/// <summary> Called when the client receives a recipes message, which
		///           contains all of the server's building recipes, loading them. </summary>
		public void LoadFromMessage(Message message)
		{
			FromBytes(message._data);
			ResolveIngredients();
		}

		/// <summary>
		/// Called when the savegame is loaded, parses building recipes from JSON assets.
		/// Recipes are loaded from "assets/recipes/buildingoverhaul/**.json".
		/// </summary>
		public void LoadFromAssets(IAssetManager manager, ILogger logger)
		{
			var count = 0;
			_byTool = new();

			void LoadRecipe(AssetLocation location, JToken token)
			{
				var recipe = token.ToObject<Recipe>(location.Domain);
				if (!recipe.Enabled) return;
				recipe.Location = location;
				// TODO: Do some validation to make it easier to spot errors?

				// Ensure that every ingredient's AllowedVariants is sorted and distinct.
				// This is to make sure the array can be easily tested for equality.
				foreach (var ingredient in new []{ recipe.Tool, recipe.Material }.Append(recipe.Ingredients))
					ingredient.AllowedVariants = ingredient.AllowedVariants?.OrderBy(x => x)?.Distinct()?.ToArray();

				// Group recipes into separate lists by their tools.
				// For example all recipes with "game:hammer-*" end up in the same group.
				var groupedRecipes = _byTool.Find(list => (recipe.Tool == list[0].Tool));
				if (groupedRecipes == null) _byTool.Add(groupedRecipes = new());
				groupedRecipes.Add(recipe);
				count++;
			}

			var assets = manager.GetMany<JToken>(logger, $"recipes/{BuildingOverhaulSystem.MOD_ID}/");
			foreach (var asset in assets) switch (asset.Value) {
				case JObject obj: LoadRecipe(asset.Key, obj); break;
				case JArray arr: foreach (var token in arr) LoadRecipe(asset.Key, token); break;
			}

			ResolveIngredients();
			CachedMessage = new Message { _data = ToBytes() };
			logger.Event("{0} building recipes loaded", count);
		}

		public void ResolveIngredients()
		{
			var world = BuildingOverhaulSystem.API.World;

			// Collect all unique wildcard ingredients as keys in the ingredient lookup dictionary.
			_ingredientLookup = _byTool
				.SelectMany(recipesByTool => recipesByTool)
				.SelectMany(recipe => new []{ recipe.Tool, recipe.Material, recipe.Output }.Concat(recipe.Ingredients))
				.Where(ingredient => ingredient.Code.IsWildCard)
				.Distinct()
				.ToDictionary(i => i, _ => new List<CollectibleObject>());

			// Go through all blocks and items in the game and match them up with the ingredients.
			foreach (var collectible in world.Blocks.Concat<CollectibleObject>(world.Items)) {
				if (collectible?.IsMissing != false) continue;
				foreach (var kvp in _ingredientLookup) {
					if (kvp.Key.Matches(collectible)) {
						kvp.Value.Add(collectible);
						break;
					}
				}
			}
		}


		// ===============================
		// == Reading / writing recipes ==
		// ===============================

		private void FromBytes(byte[] data)
			=> Read(new BinaryReader(new MemoryStream(data)));
		private byte[] ToBytes()
		{
			var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);
			Write(writer);
			return ms.ToArray();
		}

		private void Read(BinaryReader reader)
		{
			_byTool = new List<List<Recipe>>(reader.ReadInt32());
			for (var i = 0; i < _byTool.Capacity; i++) {
				var recipes = new List<Recipe>(reader.ReadInt32());
				for (var j = 0; j < recipes.Capacity; j++)
					recipes.Add(ReadRecipe(reader));
				_byTool.Add(recipes);
			}
		}
		private void Write(BinaryWriter writer)
		{
			writer.Write(_byTool.Count);
			foreach (var recipes in _byTool) {
				writer.Write(recipes.Count);
				foreach (var recipe in recipes)
					WriteRecipe(writer, recipe);
			}
		}

		private static Recipe ReadRecipe(BinaryReader reader) => new()
		{
			Location = new AssetLocation(reader.ReadString()),
			Shape    = reader.ReadString(),
			Tool     = ReadIngredient(reader),
			Material = ReadIngredient(reader),
			Ingredients = Enumerable.Range(0, reader.ReadByte())
				.Select(i => ReadIngredient(reader)).ToArray(),
			Output = ReadIngredient(reader),
			ToolDurabilityCost = reader.ReadInt32(),
		};
		private static void WriteRecipe(BinaryWriter writer, Recipe value)
		{
			writer.Write(value.Location.ToString());
			writer.Write(value.Shape);
			WriteIngredient(writer, value.Tool);
			WriteIngredient(writer, value.Material);
			writer.Write((byte)value.Ingredients.Length);
			foreach (var ingredient in value.Ingredients)
				WriteIngredient(writer, ingredient);
			WriteIngredient(writer, value.Output);
			// Enabled is assumed to be true, client should only receive enabled recipes.
			writer.Write(value.ToolDurabilityCost);
		}

		private static Ingredient ReadIngredient(BinaryReader reader) => new()
		{
			Type = (EnumItemClass)reader.ReadByte(),
			Code = new AssetLocation(reader.ReadString()),
			AllowedVariants = reader.ReadBoolean() ? reader.ReadStringArray() : null,
			Name       = reader.ReadBoolean() ? reader.ReadString() : null,
			Quantity   = reader.ReadInt32(),
			Attributes = ReadAttributes(reader),
		};
		private static void WriteIngredient(BinaryWriter writer, Ingredient value)
		{
			writer.Write((byte)value.Type);
			writer.Write(value.Code.ToString());
			writer.Write(value.AllowedVariants != null);
			if (value.AllowedVariants != null) writer.WriteArray(value.AllowedVariants);
			writer.Write(value.Name != null);
			if (value.Name != null) writer.Write(value.Name);
			writer.Write(value.Quantity);
			WriteAttributes(writer, value.Attributes);
		}

		private static ITreeAttribute ReadAttributes(BinaryReader reader)
		{
			var count = reader.ReadInt32();
			if (count == 0) return null;
			var bytes = reader.ReadBytes(count);
			return TreeAttribute.CreateFromBytes(bytes);
		}
		private static void WriteAttributes(BinaryWriter writer, ITreeAttribute value)
		{
			if (value != null) {
				var bytes = ((TreeAttribute)value).ToBytes();
				writer.Write(bytes.Length);
				writer.Write(bytes);
			} else writer.Write(0);
		}
	}

	/// <summary> A matched recipe with its ingredients and outputs resolved. </summary>
	public class RecipeMatch
	{
		public Recipe Recipe { get; }
		public List<List<ItemStack>> Ingredients { get; }
		public ItemStack Output { get; }
		public RecipeMatch(Recipe recipe, List<List<ItemStack>> ingredients, ItemStack output)
			{ Recipe = recipe; Ingredients = ingredients; Output = output; }
	}

	static class AssetLocationExtensions
	{
		/// <summary>
		/// Applies "name => code" mapping to this AssetLocation's path, if
		/// the specified wildcard ingredient has a name associated with it.
		/// </summary>
		internal static AssetLocation ApplyMapping(this AssetLocation loc,
			Ingredient wildcard, ItemStack stack)
		{
			if (wildcard.Name == null) return loc;
			var recipePath = wildcard.Code.Path;
			var actualPath = stack.Collectible.Code.Path;
			// Find the index of the wildcard in the ingredient ("game:plank-*").
			var wildcardIndex = recipePath.IndexOf('*');
			// Extract the code ("acacia") from the stack ("game:plank-acacia").
			var code = actualPath.Substring(wildcardIndex, actualPath.Length - recipePath.Length + 1);
			// Apply the mapping so "game:plank-{wood}" will turn into "game:plank-acacia".
			var path = loc.Path.Replace("{" + wildcard.Name + "}", code);
			return new(loc.Domain, path);
		}
	}
}

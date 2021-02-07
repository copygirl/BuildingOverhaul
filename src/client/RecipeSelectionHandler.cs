using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace BuildingOverhaul.Client
{
	/// <summary>
	/// Handles and contains the currently matched and selected recipes as well as
	/// the selected shape on the client. When the held items change, it recalculates
	/// <see cref="MatchedRecipes"/> and fires <see cref="MatchedRecipesChanged"/>.
	/// <para/>
	/// The <see cref="CurrentShape"/> property can be set to change which of
	/// the matched recipes is selected (accessible by <see cref="CurrentRecipe"/>).
	/// There are additional events fired when these properties change, as well.
	/// </summary>
	public class RecipeSelectionHandler
	{
		private readonly ICoreClientAPI _api;
		private readonly BuildingRecipes _recipes;
		private ItemStack? _prevToolStack;
		private ItemStack? _prevMaterialStack;

		private string _currentShape = "block";
		private List<RecipeMatch> _matchedRecipes = new();
		private RecipeMatch? _currentRecipe = null;

		/// <summary>
		/// Gets or sets the currently selected shape, such as "block" or "stair".
		/// </summary>
		public string CurrentShape {
			get => _currentShape;
			set => OnPropertySet(ref _currentShape, value, CurrentShapeChanged);
		}

		/// <summary>
		/// Gets the recipes matching the currently held tool and material stacks.
		/// (That is, the player's offhand and selected hotbar slot, respectively).
		/// </summary>
		public List<RecipeMatch> MatchedRecipes {
			get => _matchedRecipes;
			private set => OnPropertySet(ref _matchedRecipes, value, OnMatchedRecipesChanged);
		}

		/// <summary>
		/// Gets the currently selected recipe, which depends
		/// on the currently matched recipes and selected shape.
		/// </summary>
		public RecipeMatch? CurrentRecipe {
			get => _currentRecipe;
			private set => OnPropertySet(ref _currentRecipe, value, CurrentRecipeChanged);
		}

		public event System.Action<string>? CurrentShapeChanged;
		public event System.Action<List<RecipeMatch>>? MatchedRecipesChanged;
		public event System.Action<RecipeMatch?>? CurrentRecipeChanged;

		public RecipeSelectionHandler(ICoreClientAPI api, BuildingRecipes recipes)
		{
			_api     = api;
			_recipes = recipes;
			CurrentShapeChanged = _ => UpdateCurrentRecipe();
			api.Event.RegisterGameTickListener(OnGameTick, 0);
		}

		private void OnMatchedRecipesChanged(List<RecipeMatch> matches)
		{
			// This is to make sure the MatchedRecipesChanged event fires
			// completely before the CurrentRecipeChanged event fires itself.
			// Without this, handlers might work with outdated data.
			MatchedRecipesChanged?.Invoke(matches);
			UpdateCurrentRecipe();
		}

		private void UpdateCurrentRecipe()
			=> CurrentRecipe = MatchedRecipes.Find(r => r.Recipe.Shape == CurrentShape);


		private void OnGameTick(float delta)
		{
			var player        = _api.World.Player;
			var toolStack     = player.Entity.LeftHandItemSlot.Itemstack;
			var materialStack = player.Entity.RightHandItemSlot.Itemstack;

			// If held items change, attempt to get new recipes, closing if no recipes were found.
			if ((toolStack != _prevToolStack) || (materialStack != _prevMaterialStack)) {
				_prevToolStack     = toolStack;
				_prevMaterialStack = materialStack;

				var newRecipes = _recipes.Find(toolStack, materialStack);
				// Don't do anything if this list happens to match our current one.
				if (newRecipes.SequenceEqual(MatchedRecipes)) return;
				MatchedRecipes = newRecipes;
			}
		}


		/// <summary>
		/// A helper function called when a property is set. It checks to make
		/// sure the value is actually changed, and if so sets the underlying
		/// field, and calls the specified action.
		/// </summary>
		private static void OnPropertySet<T>(ref T field, T value, System.Action<T>? action)
		{
			if (EqualityComparer<T>.Default.Equals(value, field)) return;
			field = value;
			action?.Invoke(value);
		}
	}
}

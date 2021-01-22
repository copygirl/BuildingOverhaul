using System;
using System.Collections.Generic;
using System.Linq;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace BuildingOverhaul
{
	public class GuiDialogShapeSelector : GuiDialogGeneric
	{
		public const string DIALOG_NAME = BuildingOverhaulSystem.MOD_ID + ":selectshape";
		private static double SLOT_SIZE = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGrid.unscaledSlotPadding;

		// TODO: If either of the held stacks changes, recalculate recipes, closing dialog if none were found.
		public ItemStack ToolStack { get; }
		public ItemStack MaterialStack { get; }
		public List<RecipeMatch> Recipes { get; }

		private List<SkillItem> RecipeItems { get; }
		private List<ItemStack> IngredientItems { get; } = new(){  };
		private GuiElementSkillItemGrid RecipeGrid { get; set; }

		/// <summary> Called when a recipe is selected in the grid, closing the dialog afterwards. </summary>
		public event System.Action<string> OnShapeSelected;

		public GuiDialogShapeSelector(ICoreClientAPI capi, BuildingRecipes recipes, string currentShape)
			: base(Lang.Get(DIALOG_NAME), capi)
		{
			var inventory = capi.World.Player.InventoryManager;
			ToolStack     = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName)[10].Itemstack;
			MaterialStack = inventory.ActiveHotbarSlot.Itemstack;
			Recipes       = recipes.Find(ToolStack, MaterialStack);

			RecipeItems = Recipes
				.Select(recipe => new SkillItem {
					Code = recipe.Output.Collectible.Code,
					Name = recipe.Output.GetName(),
					Data = recipe.Output.StackSize,
					RenderHandler = (code, dt, posX, posY) => {
						var size = GuiElement.scaled(SLOT_SIZE - 5);
						capi.Render.RenderItemstackToGui(new DummySlot(recipe.Output), posX + size / 2, posY + size / 2, 100,
							(float)GuiElement.scaled(GuiElementPassiveItemSlot.unscaledItemSize), ColorUtil.WhiteArgb);
					}
				}).ToList();

			if (Recipes.Count > 0)
				SetupDialog(currentShape);
		}

		const int NAME_OFFSET        =  5; const int NAME_HEIGHT       = 28;
		const int INGREDIENT_OFFSET  = 33; const int INGREDIENT_HEIGHT = 18;
		const int INGREDIENTS_OFFSET = 54;
		private void SetupDialog(string currentShape)
		{
			var SLOTS_PER_ROW = 8;
			var columns = Math.Min(SLOTS_PER_ROW, Recipes.Count);
			var rows    = (Recipes.Count + (SLOTS_PER_ROW - 1)) / SLOTS_PER_ROW;
			var innerWidth = SLOTS_PER_ROW * SLOT_SIZE;

			var recipesBounds     = ElementBounds.Fixed(0, 30, innerWidth, rows * SLOT_SIZE);
			var nameBounds        = ElementBounds.Fixed(0, 30 + rows * SLOT_SIZE + NAME_OFFSET, innerWidth, NAME_HEIGHT);
			var ingredientBounds  = ElementBounds.Fixed(0, 30 + rows * SLOT_SIZE + INGREDIENT_OFFSET, innerWidth - 2, INGREDIENT_HEIGHT);
			var ingredientsBounds = ElementBounds.Fixed(0, 30 + rows * SLOT_SIZE + INGREDIENTS_OFFSET, innerWidth, SLOT_SIZE);
			var backgroundBounds  = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
			backgroundBounds.BothSizing = ElementSizing.FitToChildren;

			SingleComposer = capi.Gui
				.CreateCompo(DIALOG_NAME, ElementStdBounds.AutosizedMainDialog)
				.AddShadedDialogBG(backgroundBounds, true)
				.AddDialogTitleBar(Lang.Get(DIALOG_NAME), () => TryClose())
				.BeginChildElements(backgroundBounds)
					// Recipe grid shows the available recipes for this tool and material stack.
					.AddSkillItemGrid(RecipeItems, columns, rows, OnSlotClick, recipesBounds, "recipegrid")
					// Name text shows the human-readable (and translated) name of the selected recipe's output.
					.AddDynamicText("", CairoFont.WhiteSmallishText(), EnumTextOrientation.Left, nameBounds, "name")
					// Static "Building Cost:" text.
					.AddStaticText(Lang.Get(BuildingOverhaulSystem.MOD_ID + ":ingredients"),
					               CairoFont.WhiteDetailText(), EnumTextOrientation.Left, ingredientBounds)
					// List of required ingredients that can be hovered to see their tooltip.
					.AddRichtext(new []{ new IngredientsTextComponent(capi, IngredientItems) }, ingredientsBounds, "ingredients")
				.EndChildElements()
				.Compose();

			RecipeGrid = SingleComposer.GetSkillItemGrid("recipegrid");
			RecipeGrid.OnSlotOver = OnSlotOver;

			var index = Recipes.FindIndex(match => match.Recipe.Shape == currentShape);
			if (index >= 0) OnSlotOver(index);
		}

		public override bool TryOpen()
			=> (Recipes.Count > 0) && base.TryOpen();

		private void OnSlotOver(int index)
		{
			if (index >= Recipes.Count) return;

			// OnSlotOver appears to be called every frame, potentially to allow for custom rendering code.
			// Since updating the elements might be slightly expensive, we only want to know when it changes.
			if (index == RecipeGrid.selectedIndex) return;
			RecipeGrid.selectedIndex = index;

			IngredientItems.Clear();
			IngredientItems.AddRange(Recipes[index].Ingredients);

			SingleComposer.GetDynamicText("name").SetNewText(RecipeItems[index].Name);
			SingleComposer.GetRichtext("ingredients").RecomposeText();
		}

		private void OnSlotClick(int index)
		{
			// TODO: Click to select only (OnSlotOver does nothing), but don't close.
			//       Press tool mode selection hotkey again to close instead.
			OnShapeSelected?.Invoke(Recipes[index].Recipe.Shape);
			TryClose();
		}
	}

	public class IngredientsTextComponent : ItemstackComponentBase
	{
		public List<ItemStack> Items { get; }

		private DummySlot _slot = new();
		private double _size = 40;

		// TODO: Rotate matching ingredients.
		// float secondsVisible = 1;
		// int curItemIndex;

		public IngredientsTextComponent(ICoreClientAPI api, List<ItemStack> items)
			: base(api) { Items = items; }

		public override bool CalcBounds(TextFlowPath[] flowPath, double currentLineHeight, double lineX, double lineY)
		{
			var width = Math.Max(1.0, Items.Count * (_size + 3));
			BoundsPerLine = new LineRectangled[]{ new(0, 0, width, _size + 3) };
			return false;
		}

		public override void ComposeElements(Context ctx, ImageSurface surface)
		{
			ctx.SetSourceRGBA(1, 1, 1, 0.2);
			for (var i = 0; i < Items.Count; i++) {
				ctx.Rectangle(i * (_size + 3), 0, _size, _size);
				ctx.Fill();
			}
		}

		public override void RenderInteractiveElements(float deltaTime, double renderX, double renderY)
		{
			// if ((secondsVisible -= deltaTime) <= 0) {
			// 	secondsVisible = 1;
			// 	curItemIndex = (curItemIndex + 1) % GridRecipes.Length;
			// }

			for (var i = 0; i < Items.Count; i++) {
				_slot.Itemstack = Items[i];
				var rx = renderX + i * (_size + 3);
				var ry = renderY;

				api.Render.RenderItemstackToGui(_slot,
					rx + _size * 0.5f, ry + _size * 0.5f, 100,
					(float)_size * 0.58f, ColorUtil.WhiteArgb);

				double dx = api.Input.MouseX - rx;
				double dy = api.Input.MouseY - ry;
				if ((dx >= 0) && (dx <= _size) && (dy >= 0) && (dy <= _size)) {
					RenderItemstackTooltip(_slot, rx + dx, ry + dy, deltaTime);
					capi.Render.GlScissorFlag(false);
				}
			}
		}
	}
}

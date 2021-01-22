using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

		const int SLOTS_PER_ROW = 8;
		static double SLOT_SIZE = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGrid.unscaledSlotPadding;
		const int NAME_OFFSET        =  5; const int NAME_HEIGHT       = 28;
		const int INGREDIENT_OFFSET  = 33; const int INGREDIENT_HEIGHT = 18;
		const int INGREDIENTS_OFFSET = 54;


		private BuildingRecipes Recipes { get; }
		private List<SkillItem> RecipeItems { get; } = new(){  };
		private List<ItemStack> IngredientItems { get; } = new(){  };
		private GuiElementSkillItemGrid RecipeGrid { get; set; }

		public string CurrentShape { get; private set; } = "block";
		public ItemStack ToolStack { get; private set; }
		public ItemStack MaterialStack { get; private set; }
		public List<RecipeMatch> MatchedRecipes { get; private set; }

		private readonly ElementBounds _recipesBounds;
		private readonly ElementBounds _nameBounds;
		private readonly ElementBounds _ingredientBounds;
		private readonly ElementBounds _ingredientsBounds;

		private readonly FieldInfo _colsField = typeof(GuiElementSkillItemGrid).GetField("cols", BindingFlags.Instance | BindingFlags.NonPublic);
		private readonly FieldInfo _rowsField = typeof(GuiElementSkillItemGrid).GetField("rows", BindingFlags.Instance | BindingFlags.NonPublic);

		public GuiDialogShapeSelector(ICoreClientAPI capi, BuildingRecipes recipes)
			: base(Lang.Get(DIALOG_NAME), capi)
		{
			Recipes = recipes;

			var columns = Math.Min(SLOTS_PER_ROW, 1);
			var rows    = (1 + (SLOTS_PER_ROW - 1)) / SLOTS_PER_ROW;
			var innerWidth = SLOTS_PER_ROW * SLOT_SIZE;

			_recipesBounds     = ElementBounds.Fixed(0, 30, innerWidth, rows * SLOT_SIZE);
			_nameBounds        = ElementBounds.Fixed(0, 30 + rows * SLOT_SIZE + NAME_OFFSET, innerWidth, NAME_HEIGHT);
			_ingredientBounds  = ElementBounds.Fixed(0, 30 + rows * SLOT_SIZE + INGREDIENT_OFFSET, innerWidth - 2, INGREDIENT_HEIGHT);
			_ingredientsBounds = ElementBounds.Fixed(0, 30 + rows * SLOT_SIZE + INGREDIENTS_OFFSET, innerWidth, SLOT_SIZE);
			var backgroundBounds  = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
			backgroundBounds.BothSizing = ElementSizing.FitToChildren;

			SingleComposer = capi.Gui
				.CreateCompo(DIALOG_NAME, ElementStdBounds.AutosizedMainDialog)
				.AddShadedDialogBG(backgroundBounds, true)
				.AddDialogTitleBar(Lang.Get(DIALOG_NAME), () => TryClose())
				.BeginChildElements(backgroundBounds)
					// Recipe grid shows the available recipes for this tool and material stack.
					.AddSkillItemGrid(RecipeItems, columns, rows, OnSlotClick, _recipesBounds, "recipegrid")
					// Name text shows the human-readable (and translated) name of the selected recipe's output.
					.AddDynamicText("", CairoFont.WhiteSmallishText(), EnumTextOrientation.Left, _nameBounds, "name")
					// Static "Building Cost:" text.
					.AddStaticText(Lang.Get(BuildingOverhaulSystem.MOD_ID + ":ingredients"),
					               CairoFont.WhiteDetailText(), EnumTextOrientation.Left, _ingredientBounds)
					// List of required ingredients that can be hovered to see their tooltip.
					.AddRichtext(new []{ new IngredientsTextComponent(capi, IngredientItems) }, _ingredientsBounds, "ingredients")
				.EndChildElements()
				.Compose();

			RecipeGrid = SingleComposer.GetSkillItemGrid("recipegrid");
		}

		public override void OnRenderGUI(float deltaTime)
		{
			var inventory     = capi.World.Player.InventoryManager;
			var toolStack     = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName)[10].Itemstack;
			var materialStack = inventory.ActiveHotbarSlot.Itemstack;

			// If held items change, attempt to get new recipes, closing if no recipes were found.
			if ((toolStack != ToolStack) || (materialStack != MaterialStack))
			if (!TryOpen()) { TryClose(); return; }

			base.OnRenderGUI(deltaTime);
		}

		public override bool TryOpen()
		{
			var inventory  = capi.World.Player.InventoryManager;
			ToolStack      = inventory.GetOwnInventory(GlobalConstants.hotBarInvClassName)[10].Itemstack;
			MaterialStack  = inventory.ActiveHotbarSlot.Itemstack;
			MatchedRecipes = Recipes.Find(ToolStack, MaterialStack);
			if (MatchedRecipes.Count == 0) return false;

			RecipeItems.Clear();
			RecipeItems.AddRange(MatchedRecipes.Select(recipe => new SkillItem {
				Code = recipe.Output.Collectible.Code,
				Name = recipe.Output.GetName(),
				Data = recipe.Output.StackSize,
				RenderHandler = (code, dt, posX, posY) => {
					var size = GuiElement.scaled(SLOT_SIZE - 5);
					capi.Render.RenderItemstackToGui(new DummySlot(recipe.Output), posX + size / 2, posY + size / 2, 100,
						(float)GuiElement.scaled(GuiElementPassiveItemSlot.unscaledItemSize), ColorUtil.WhiteArgb);
				}
			}));

			_colsField.SetValue(RecipeGrid, Math.Min(SLOTS_PER_ROW, MatchedRecipes.Count));
			_rowsField.SetValue(RecipeGrid, (MatchedRecipes.Count + (SLOTS_PER_ROW - 1)) / SLOTS_PER_ROW);

			RecipeGrid.selectedIndex = -2;
			OnSlotClick(MatchedRecipes.FindIndex(match => match.Recipe.Shape == CurrentShape));

			RecalculateBounds();
			SingleComposer.ReCompose();
			return base.TryOpen();
		}

		private void RecalculateBounds()
		{
			var columns = Math.Min(SLOTS_PER_ROW, MatchedRecipes.Count);
			var rows    = (MatchedRecipes.Count + (SLOTS_PER_ROW - 1)) / SLOTS_PER_ROW;

			_recipesBounds.fixedY      = 30;
			_recipesBounds.fixedWidth  = columns * SLOT_SIZE;
			_recipesBounds.fixedHeight = rows    * SLOT_SIZE;

			_nameBounds       .fixedY = 30 + rows * SLOT_SIZE + NAME_OFFSET;
			_ingredientBounds .fixedY = 30 + rows * SLOT_SIZE + INGREDIENT_OFFSET;
			_ingredientsBounds.fixedY = 30 + rows * SLOT_SIZE + INGREDIENTS_OFFSET;
		}

		private void OnSlotClick(int index)
		{
			if (index >= MatchedRecipes.Count) return;
			if (index == RecipeGrid.selectedIndex) return;
			RecipeGrid.selectedIndex = index;

			IngredientItems.Clear();
			IngredientItems.AddRange((index >= 0) ? MatchedRecipes[index].Ingredients : new());
			SingleComposer.GetDynamicText("name").SetNewText((index >= 0) ? RecipeItems[index].Name : "");
			SingleComposer.GetRichtext("ingredients").RecomposeText();
			if (index >= 0) CurrentShape = MatchedRecipes[index].Recipe.Shape;
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

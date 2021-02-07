using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace BuildingOverhaul.Client
{
	public class GuiDialogShapeSelector : GuiDialogGeneric
	{
		public const string DIALOG_NAME = BuildingOverhaulSystem.MOD_ID + ":selectshape";

		const int SLOTS_PER_ROW = 8;
		static double SLOT_SIZE = GuiElementPassiveItemSlot.unscaledSlotSize + GuiElementItemSlotGrid.unscaledSlotPadding;
		const int NAME_OFFSET        =  5; const int NAME_HEIGHT       = 28;
		const int INGREDIENT_OFFSET  = 33; const int INGREDIENT_HEIGHT = 18;
		const int INGREDIENTS_OFFSET = 54;

		private readonly BuildingRecipes _recipes;
		private readonly RecipeSelectionHandler _selection;
		private GuiElementSkillItemGrid _recipeGrid;
		private List<SkillItem> _skillItems = new();
		private List<int> _missingIngredients = new();

		private readonly ElementBounds _recipesBounds;
		private readonly ElementBounds _nameBounds;
		private readonly ElementBounds _ingredientBounds;
		private readonly ElementBounds _ingredientsBounds;

		private readonly FieldInfo _rowsField = typeof(GuiElementSkillItemGrid)
			.GetField("rows", BindingFlags.Instance | BindingFlags.NonPublic);

		public override bool PrefersUngrabbedMouse => false;

		public GuiDialogShapeSelector(ICoreClientAPI capi,
			BuildingRecipes recipes, RecipeSelectionHandler selection)
			: base(Lang.Get(DIALOG_NAME), capi)
		{
			_recipes   = recipes;
			_selection = selection;
			capi.Gui.RegisterDialog(this);

			_selection.MatchedRecipesChanged += OnMatchedRecipesChanged;
			_selection.CurrentRecipeChanged  += OnCurrentRecipeChanged;

			var innerWidth = SLOTS_PER_ROW * SLOT_SIZE;
			_recipesBounds     = ElementBounds.Fixed(0, 0, innerWidth, 0);
			_nameBounds        = ElementBounds.Fixed(0, 0, innerWidth, NAME_HEIGHT);
			_ingredientBounds  = ElementBounds.Fixed(0, 0, innerWidth - 2, INGREDIENT_HEIGHT);
			_ingredientsBounds = ElementBounds.Fixed(0, 0, innerWidth, SLOT_SIZE);
			var backgroundBounds  = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
			backgroundBounds.BothSizing = ElementSizing.FitToChildren;

			SingleComposer = capi.Gui
				.CreateCompo(DIALOG_NAME, ElementStdBounds.AutosizedMainDialog
					.WithFixedAlignmentOffset(0, 220))
				.AddShadedDialogBG(backgroundBounds, true)
				.AddDialogTitleBar(Lang.Get(DIALOG_NAME), () => TryClose())
				.BeginChildElements(backgroundBounds)
					// Recipe grid shows the available recipes for this tool and material stack.
					.AddSkillItemGrid(_skillItems, SLOTS_PER_ROW, 1, OnSlotClick, _recipesBounds, "recipegrid")
					// Name text shows the human-readable (and translated) name of the selected recipe's output.
					.AddDynamicText("", CairoFont.WhiteSmallishText(), EnumTextOrientation.Left, _nameBounds, "name")
					// Static "Building Cost:" text.
					.AddStaticText(Lang.Get(BuildingOverhaulSystem.MOD_ID + ":ingredients"),
					               CairoFont.WhiteDetailText(), EnumTextOrientation.Left, _ingredientBounds)
					// List of required ingredients that can be hovered to see their tooltip.
					.AddRichtext(new []{ new IngredientsTextComponent(capi, this) }, _ingredientsBounds, "ingredients")
				.EndChildElements()
				.Compose();

			_recipeGrid = SingleComposer.GetSkillItemGrid("recipegrid");
		}

		public override bool TryOpen()
			=> (_selection.MatchedRecipes.Count > 0) && base.TryOpen();


		private void OnMatchedRecipesChanged(List<RecipeMatch> matchedRecipes)
		{
			if ((matchedRecipes.Count == 0) && TryClose()) return;

			_skillItems.Clear();
			_skillItems.AddRange(matchedRecipes.Select(recipe => new SkillItem {
				Code = recipe.Output.Collectible.Code,
				Name = recipe.Output.GetName(),
				Data = recipe.Output.StackSize,
				RenderHandler = (code, dt, posX, posY) => {
					var size = GuiElement.scaled(SLOT_SIZE - 5);
					capi.Render.RenderItemstackToGui(new DummySlot(recipe.Output), posX + size / 2, posY + size / 2, 100,
						(float)GuiElement.scaled(GuiElementPassiveItemSlot.unscaledItemSize), ColorUtil.WhiteArgb);
				}
			}));

			var rows = (matchedRecipes.Count + (SLOTS_PER_ROW - 1)) / SLOTS_PER_ROW;
			_rowsField.SetValue(_recipeGrid, rows);

			RecalculateBounds();
			SingleComposer.ReCompose();
		}

		private void OnCurrentRecipeChanged(RecipeMatch? currentRecipe)
		{
			var index = _selection.MatchedRecipes.IndexOf(currentRecipe!);
			_recipeGrid.selectedIndex = index;

			SingleComposer.GetDynamicText("name").SetNewText((index >= 0) ? _skillItems[index].Name : "");
			SingleComposer.GetRichtext("ingredients").RecomposeText();
		}


		public override void OnRenderGUI(float deltaTime)
		{
			// If a recipe is currently selected, check to
			// see which ingredients we're missing (if any).
			if (_selection.CurrentRecipe != null)
				_recipes.FindIngredients(capi.World.Player,
					_selection.CurrentRecipe, _missingIngredients);

			base.OnRenderGUI(deltaTime);
		}

		private void RecalculateBounds()
		{
			var rows = (_skillItems.Count + (SLOTS_PER_ROW - 1)) / SLOTS_PER_ROW;

			_recipesBounds.fixedY      = 30;
			_recipesBounds.fixedHeight = rows * SLOT_SIZE;

			_nameBounds       .fixedY = 30 + rows * SLOT_SIZE + NAME_OFFSET;
			_ingredientBounds .fixedY = 30 + rows * SLOT_SIZE + INGREDIENT_OFFSET;
			_ingredientsBounds.fixedY = 30 + rows * SLOT_SIZE + INGREDIENTS_OFFSET;
		}

		private void OnSlotClick(int index)
		{
			if (index >= _skillItems.Count) return;
			_selection.CurrentShape = _selection.MatchedRecipes[index].Recipe.Shape;
		}

		public override void OnMouseWheel(MouseWheelEventArgs args)
		{
			if (!SingleComposer.Bounds.PointInside(capi.Input.MouseX, capi.Input.MouseY)) return;
			var count = _skillItems.Count;
			var index = (Math.Max(0, _recipeGrid.selectedIndex) + count - args.delta) % count;
			_selection.CurrentShape = _selection.MatchedRecipes[index].Recipe.Shape;
			args.SetHandled(true);
		}


		public class IngredientsTextComponent : ItemstackComponentBase
		{
			private static readonly List<List<ItemStack>> _emptyItems = new();

			public GuiDialogShapeSelector Dialog { get; }
			public List<List<ItemStack>> Items => Dialog._selection
				.CurrentRecipe?.Ingredients ?? _emptyItems;

			private DummySlot _slot = new();
			private double _size = 40;

			private float _secondsVisible = 1.0F;
			private int _currentItemIndex = 1;

			public IngredientsTextComponent(ICoreClientAPI api, GuiDialogShapeSelector dialog)
				: base(api) { Dialog = dialog; }

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
				if ((_secondsVisible -= deltaTime) <= 0) {
					_currentItemIndex++;
					_secondsVisible = 1.0F;
				}

				for (var i = 0; i < Items.Count; i++) {
					_slot.Itemstack = Items[i][_currentItemIndex % Items[i].Count];
					var rx = renderX + i * (_size + 3);
					var ry = renderY;

					if (Dialog._missingIngredients[i] > 0) {
						var color = ColorUtil.ToRgba(255 / 5, 255, 0, 0);
						api.Render.RenderRectangle((float)rx, (float)ry, 80.0F, (float)_size, (float)_size, color);
						api.Render.RenderRectangle((float)rx + 1, (float)ry + 1, 80.0F, (float)_size - 2, (float)_size - 2, color);
					}

					api.Render.RenderItemstackToGui(_slot,
						rx + _size * 0.5F, ry + _size * 0.5F, 100.0F,
						(float)_size * 0.58F, ColorUtil.WhiteArgb);

					// Something from RenderRectangle causes stacks to be rendered all "ghostly" which
					// we want to make use of - it looks cool. But this state may be leaked to the next
					// rendered stack, even if it's not a missing ingredient, so we need to clear it.
					if (Dialog._missingIngredients[i] > 0)
						ShaderPrograms.Gui.NoTexture = 0;

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
}

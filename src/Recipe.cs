using System;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace BuildingOverhaul
{
	public class Recipe
	{
		[JsonIgnore]
		public AssetLocation Location { get; set; }

		public string Shape { get; set; }
		public Ingredient Tool { get; set; }          // Type, Name and Quantity not applicable.
		public Ingredient Material { get; set; }      // Quantity not applicable.
		public Ingredient[] Ingredients { get; set; } // Name not applicable.
		public AssetLocation Output { get; set; }

		public bool Enabled { get; set; } = true;
		public int ToolDurabilityCost { get; set; } = 1;
		// public AssetLocation ReplaceBlock { get; } = null;
	}

	public class Ingredient : IEquatable<Ingredient>
	{
		public EnumItemClass Type { get; set; } = EnumItemClass.Item;
		public AssetLocation Code { get; set; }
		public string[] AllowedVariants { get; set; } = null;

		// Name and Quantity are not relevant for equality.
		public string Name { get; set; } = null;
		public int Quantity { get; set; } = 1;

		internal bool Matches(ItemStack stack)
			=> (Type == stack?.Collectible?.ItemClass) &&
			   WildcardUtil.Match(Code, stack.Collectible.Code, AllowedVariants);

		public bool Equals(Ingredient other)
		{
			return (other != null) && (Type == other.Type) && Code.Equals(other.Code) &&
				((AllowedVariants == other.AllowedVariants) ||
				 ((AllowedVariants != null) && (other.AllowedVariants != null) &&
				  AllowedVariants.SequenceEqual(other.AllowedVariants)));
		}

		public override bool Equals(object obj)
			=> Equals(obj as Ingredient);

		public override int GetHashCode()
		{
			int hashCode = 1404618148;
			hashCode = hashCode * -1521134295 + Type.GetHashCode();
			hashCode = hashCode * -1521134295 + Code.GetHashCode();
			if (AllowedVariants != null)
			foreach (var variant in AllowedVariants)
				hashCode = hashCode * -1521134295 + variant.GetHashCode();
			return hashCode;
		}

		public static bool operator ==(Ingredient left, Ingredient right)
			=> left?.Equals(right) ?? object.ReferenceEquals(left, right);
		public static bool operator !=(Ingredient left, Ingredient right)
			=> !(left == right);
	}
}

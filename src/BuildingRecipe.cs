using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Util;

namespace BuildingOverhaul
{
	public class BuildingRecipe : IByteSerializable
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


		public void FromBytes(BinaryReader reader, IWorldAccessor resolver)
		{
			Location = new AssetLocation(reader.ReadString());
			Shape    = reader.ReadString();
			Tool     = ReadIngredient(reader);
			Material = ReadIngredient(reader);
			Ingredients = new Ingredient[reader.ReadByte()];
			for (var i = 0; i < Ingredients.Length; i++)
				Ingredients[i] = ReadIngredient(reader);
			Output  = new AssetLocation(reader.ReadString());
			Enabled = true; // Client should only receive enabled recipes.
			ToolDurabilityCost = reader.ReadInt32();
		}

		public void ToBytes(BinaryWriter writer)
		{
			writer.Write(Location.ToString());
			writer.Write(Shape);
			WriteIngredient(writer, Tool);
			WriteIngredient(writer, Material);
			writer.Write((byte)Ingredients.Length);
			foreach (var ingredient in Ingredients)
				WriteIngredient(writer, ingredient);
			writer.Write(Output.ToString());
			// Enabled is assumed to be true, client should only receive enabled recipes.
			writer.Write(ToolDurabilityCost);
		}

		private static Ingredient ReadIngredient(BinaryReader reader)
			=> new Ingredient {
				Type = (EnumItemClass)reader.ReadByte(),
				Code = new AssetLocation(reader.ReadString()),
				AllowedVariants = reader.ReadBoolean() ? reader.ReadStringArray() : null,
				Name = reader.ReadBoolean() ? reader.ReadString() : null,
				Quantity = reader.ReadInt32(),
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
		}
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
			=> (Type == stack.Collectible.ItemClass) &&
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

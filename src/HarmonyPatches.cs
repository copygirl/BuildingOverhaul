using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.Client.NoObf;

namespace BuildingOverhaul
{
	// TODO: Allow block interaction before attempting to build.
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
}

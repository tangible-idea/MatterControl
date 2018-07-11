﻿/*
Copyright (c) 2018, Lars Brubaker, John Lewin
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this
   list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice,
   this list of conditions and the following disclaimer in the documentation
   and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The views and conclusions contained in the software and documentation are those
of the authors and should not be interpreted as representing official policies,
either expressed or implied, of the FreeBSD Project.
*/

using MatterHackers.Agg.UI;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.EditableTypes;
using MatterHackers.VectorMath;
using Newtonsoft.Json;

namespace MatterHackers.MatterControl.SlicerConfiguration
{
	public class DirectionVectorField : UIField
	{
		private DropDownList dropDownList;

		public override void Initialize(int tabIndex)
		{
			var theme = ApplicationController.Instance.Theme;

			dropDownList = new DropDownList("Name".Localize(), theme.Colors.PrimaryTextColor, Direction.Down, pointSize: theme.DefaultFontSize)
			{
				BorderColor = theme.GetBorderColor(75)
			};

			dropDownList.AddItem(
				"Back".Localize(),
				JsonConvert.SerializeObject(
					new DirectionVector()
					{
						Normal = Vector3.UnitY
					}));

			dropDownList.AddItem(
				"Up".Localize(),
				JsonConvert.SerializeObject(
					new DirectionVector()
					{
						Normal = Vector3.UnitZ
					}));

			dropDownList.AddItem(
				"Right".Localize(),
				JsonConvert.SerializeObject(
					new DirectionVector()
					{
						Normal = Vector3.UnitX
					}));

			dropDownList.SelectedLabel = "Right";

			dropDownList.SelectionChanged += (s, e) =>
			{
				if (this.Value != dropDownList.SelectedValue)
				{
					this.SetValue(
						dropDownList.SelectedValue,
						userInitiated: true);
				};
			};

			this.Content = dropDownList;
		}

		public DirectionVector DirectionVector { get; private set; }

		public void SetValue(DirectionVector directionVector)
		{
			this.SetValue(
				JsonConvert.SerializeObject(directionVector),
				false);
		}

		protected override string ConvertValue(string newValue)
		{
			this.DirectionVector = JsonConvert.DeserializeObject<DirectionVector>(newValue);
			return base.ConvertValue(newValue);
		}

		protected override void OnValueChanged(FieldChangedEventArgs fieldChangedEventArgs)
		{
			if (this.Value != dropDownList.SelectedValue)
			{
				dropDownList.SelectedValue = this.Value;
			}

			base.OnValueChanged(fieldChangedEventArgs);
		}
	}
}
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MatterHackers.Agg.UI;
using MatterHackers.DataConverters3D;
using MatterHackers.Localizations;
using MatterHackers.MatterControl.DesignTools.EditableTypes;
using MatterHackers.VectorMath;

namespace MatterHackers.MatterControl.DesignTools.Operations
{
	public class ArrayLinearObject3D : ArrayObject3D
	{
		public ArrayLinearObject3D()
		{
			Name = "Linear Array".Localize();
		}

		public override bool CanFlatten => true;

		[Slider(2, 10, Easing.EaseType.Quadratic, snapDistance: 1)]
		public override IntOrExpression Count { get; set; } = 3;

		public DirectionVector Direction { get; set; } = new DirectionVector { Normal = new Vector3(1, 0, 0) };

		[Slider(0, 200, Easing.EaseType.Quadratic, useSnappingGrid: true)]
		public DoubleOrExpression Distance { get; set; } = 30;

		public override async Task Rebuild()
		{
			var rebuildLock = this.RebuildLock();
			SourceContainer.Visible = true;

			using (new CenterAndHeightMaintainer(this, MaintainFlags.Bottom))
			{
				await ApplicationController.Instance.Tasks.Execute(
					"Linear Array".Localize(),
					null,
					(reporter, cancellationToken) =>
					{
						this.DebugDepth("Rebuild");

						var newChildren = new List<IObject3D>();

						newChildren.Add(SourceContainer);

						var arrayItem = SourceContainer.Children.First();

						var distance = Distance.Value(this);
						var count = Count.Value(this);

						// add in all the array items
						for (int i = 0; i < Math.Max(count, 1); i++)
						{
							var next = arrayItem.Clone();
							next.Matrix = arrayItem.Matrix * Matrix4X4.CreateTranslation(Direction.Normal.GetNormal() * distance * i);
							newChildren.Add(next);
						}

						Children.Modify(list =>
						{
							list.Clear();
							list.AddRange(newChildren);
						});

						var updateItems = SheetObject3D.SortAndLockUpdateItems(this, (item) =>
						{
							if (!SheetObject3D.HasExpressionWithString(item, "=", true))
							{
								return false;
							}

							// WIP
							if (item.Parent == this)
							{
								// only process our children that are not the source object
								return !(item is OperationSourceObject3D);
							}
							else if (item.Parent is OperationSourceContainerObject3D)
							{
								// If we find another source container
								// Only process its children that are the source container (they will be replicated and modified correctly by the source container)
								return item is OperationSourceObject3D;
							}
							else if (item.Parent is OperationSourceObject3D operationSourceObject3D
								&& operationSourceObject3D.Parent == this)
							{
								// we don't need to rebuild our source object
								return false;
							}

							// process everything else
							return true;
						});

						var runningInterval = SheetObject3D.SendInvalidateInRebuildOrder(updateItems, InvalidateType.Properties);

						while (runningInterval.Active)
						{
							Thread.Sleep(10);
						}

						SourceContainer.Visible = false;
						rebuildLock.Dispose();
						Parent?.Invalidate(new InvalidateArgs(this, InvalidateType.Children));

						return Task.CompletedTask;
					});
			}
		}
	}
}
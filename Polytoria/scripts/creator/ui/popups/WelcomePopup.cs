// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Godot;

namespace Polytoria.Creator.UI.Popups;

public sealed partial class WelcomePopup : PopupWindowBase
{
	[Export] private Button _startButton = null!;
	[Export] private Button _closeButton = null!;
	private Window _hostWindow = null!;

	public override void _Ready()
	{
		base._Ready();
		_startButton.Pressed += QueueFree;
		_closeButton.Pressed += QueueFree;
		_hostWindow = GetParent().GetWindow();
		_hostWindow.SizeChanged += FitToViewport;
	}

	public void Open()
	{
		FitToViewport();
		_startButton.GrabFocus();
	}

	private void FitToViewport()
	{
		// Embedded windows inherit the Creator's UI scale. Scroll the content on small screens.
		PopupCenteredClamped(new Vector2I(780, 640), 0.94f);
	}

	public override void _ExitTree()
	{
		_hostWindow.SizeChanged -= FitToViewport;
		base._ExitTree();
	}
}

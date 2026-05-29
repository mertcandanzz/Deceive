using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Deceive.Properties;

namespace Deceive;

// Modal window used to configure friend status notifications. A dedicated, searchable, scrollable
// window is used instead of a tray submenu because a roster can hold hundreds of friends.
internal sealed class FriendNotificationsForm : Form
{
    private readonly List<string> _allFriends;
    private readonly HashSet<string> _tracked;
    private readonly Action<string, bool> _setTracked;
    private readonly Action<bool> _setEnabled;

    private readonly CheckBox _enabledCheckBox;
    private readonly TextBox _searchBox;
    private readonly CheckedListBox _friendsList;
    private readonly Label _countLabel;

    private bool _populating;

    public FriendNotificationsForm(
        IEnumerable<string> friends,
        HashSet<string> tracked,
        bool enabled,
        Action<bool> setEnabled,
        Action<string, bool> setTracked)
    {
        _allFriends = friends.ToList();
        _tracked = tracked;
        _setEnabled = setEnabled;
        _setTracked = setTracked;

        Text = "Friend Notifications";
        try
        {
            Icon = Resources.DeceiveIcon;
        }
        catch
        {
            // ignored; the icon is purely cosmetic
        }

        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = true;
        ClientSize = new System.Drawing.Size(380, 480);
        MinimumSize = new System.Drawing.Size(320, 320);

        _enabledCheckBox = new CheckBox
        {
            Text = "Enable friend status notifications",
            Location = new System.Drawing.Point(12, 12),
            Size = new System.Drawing.Size(356, 24),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Checked = enabled
        };
        _enabledCheckBox.CheckedChanged += (_, _) =>
        {
            _setEnabled(_enabledCheckBox.Checked);
            _searchBox!.Enabled = _enabledCheckBox.Checked;
            _friendsList!.Enabled = _enabledCheckBox.Checked;
        };

        var helpLabel = new Label
        {
            Text = "Checked friends play a sound and show a notification whenever their status changes (e.g. when they finish a game).",
            Location = new System.Drawing.Point(12, 40),
            Size = new System.Drawing.Size(356, 36),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        };

        var searchLabel = new Label
        {
            Text = "Search:",
            Location = new System.Drawing.Point(12, 84),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Left
        };

        _searchBox = new TextBox
        {
            Location = new System.Drawing.Point(64, 81),
            Size = new System.Drawing.Size(304, 23),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            Enabled = enabled
        };
        _searchBox.TextChanged += (_, _) => Populate();

        _friendsList = new CheckedListBox
        {
            Location = new System.Drawing.Point(12, 112),
            Size = new System.Drawing.Size(356, 320),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            CheckOnClick = true,
            IntegralHeight = false,
            Enabled = enabled
        };
        _friendsList.ItemCheck += OnItemCheck;

        _countLabel = new Label
        {
            Location = new System.Drawing.Point(12, 444),
            Size = new System.Drawing.Size(240, 24),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left
        };

        var closeButton = new Button
        {
            Text = "Close",
            Location = new System.Drawing.Point(293, 444),
            Size = new System.Drawing.Size(75, 28),
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.OK
        };

        AcceptButton = closeButton;
        CancelButton = closeButton;

        Controls.AddRange(new Control[] { _enabledCheckBox, helpLabel, searchLabel, _searchBox, _friendsList, _countLabel, closeButton });

        Populate();
    }

    private void Populate()
    {
        _populating = true;
        _friendsList.BeginUpdate();
        _friendsList.Items.Clear();

        var filter = _searchBox.Text.Trim();
        foreach (var friend in _allFriends)
        {
            if (filter.Length == 0 || friend.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                _friendsList.Items.Add(friend, _tracked.Contains(friend));
        }

        _friendsList.EndUpdate();
        _populating = false;
        UpdateCountLabel();
    }

    private void OnItemCheck(object sender, ItemCheckEventArgs e)
    {
        if (_populating)
            return;

        var friend = (string)_friendsList.Items[e.Index];
        var nowChecked = e.NewValue == CheckState.Checked;
        if (nowChecked)
            _tracked.Add(friend);
        else
            _tracked.Remove(friend);

        _setTracked(friend, nowChecked);

        // The list's checked state is updated after this event returns, so defer the count refresh.
        BeginInvoke((Action)UpdateCountLabel);
    }

    private void UpdateCountLabel() => _countLabel.Text = _allFriends.Count == 0
        ? "No friends loaded yet — log in first."
        : $"{_tracked.Count} of {_allFriends.Count} friends tracked.";
}

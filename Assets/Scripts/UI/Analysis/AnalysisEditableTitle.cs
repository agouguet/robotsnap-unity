using System;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// The heading of a panel that names what the panel is about - the session shown beside the episode list, the
/// episode shown above its trajectory - and the pencil that renames it.
///
/// A dashboard is scanned by its headings, so the name a session or an episode was given has to sit where the
/// panel's own title sits, at the size of a heading, rather than in a settings screen somewhere else. The
/// pencil is what makes that name editable in place: the label it replaces is a reading, and the two exchange
/// places without the panel being rebuilt.
///
/// The heading commits on Enter and on focus leaving the field, because both are the reader saying the value
/// is what they mean, and throws the edit away on Escape, because backing out of a keystroke is not a rename.
/// What a committed value then means belongs to the caller: this only reports it, so the rule for naming an
/// episode and the rule for naming a session can differ without this having to know either.
/// </summary>
public sealed class AnalysisEditableTitle : VisualElement
{
    private readonly Label _label;
    private readonly TextField _editor;
    private readonly Button _pencil;
    private bool _editing;

    public AnalysisEditableTitle()
    {
        AddToClassList("analysis-title");
        style.display = DisplayStyle.None;

        _label = new Label();
        _label.AddToClassList("analysis-title-label");
        Add(_label);

        _editor = new TextField();
        _editor.AddToClassList("analysis-title-field");
        _editor.RegisterCallback<KeyDownEvent>(OnKeyDown);
        // Losing the focus is a decision too: a reader who clicks away has typed the name they wanted.
        _editor.RegisterCallback<FocusOutEvent>(_ => CommitEdit());
        _editor.style.display = DisplayStyle.None;
        Add(_editor);

        _pencil = new Button(BeginEdit) { text = string.Empty };
        _pencil.AddToClassList("analysis-title-pencil");
        _pencil.tooltip = "Rename";
        Add(_pencil);
    }

    /// <summary>
    /// Raised with the name the reader committed, trimmed, or <c>null</c> when they emptied the field - which
    /// is how a name is taken away again and the heading falls back to the label computed from the data.
    /// </summary>
    public event Action<string> RenameRequested;

    /// <summary>The text the heading shows while nothing is being edited.</summary>
    public string Value => _label.text;

    /// <summary>True while the pencil has swapped the label for the text field.</summary>
    public bool IsEditing => _editing;

    /// <summary>
    /// The field the pencil swaps in. Exposed so a test can type a name and commit it without a live input
    /// system, which is the one part of this control a headless test cannot drive through events.
    /// </summary>
    public TextField Editor => _editor;

    /// <summary>The pencil itself, so a caller - or a test - can find it the way a reader would.</summary>
    public Button Pencil => _pencil;

    /// <summary>
    /// Shows one text on the heading. An empty text hides the heading altogether, pencil included: a panel that
    /// is about nothing has no name to carry, and a lone pencil would be a control with nothing to act on.
    /// </summary>
    public void Show(string text)
    {
        EndEdit();
        _label.text = text ?? string.Empty;
        style.display = string.IsNullOrEmpty(_label.text) ? DisplayStyle.None : DisplayStyle.Flex;
    }

    /// <summary>Swaps the label for the text field, focused and holding the current name - what the pencil does.</summary>
    public void BeginEdit()
    {
        if (_editing || style.display == DisplayStyle.None)
            return;

        _editing = true;
        _editor.SetValueWithoutNotify(_label.text);
        _label.style.display = DisplayStyle.None;
        _editor.style.display = DisplayStyle.Flex;
        _editor.Focus();
    }

    /// <summary>Keeps what the field holds - Enter, or the field losing the focus, is a decision.</summary>
    public void CommitEdit()
    {
        if (!_editing)
            return;

        string value = _editor.value?.Trim();
        EndEdit();
        _label.text = value ?? string.Empty;
        style.display = string.IsNullOrEmpty(_label.text) ? DisplayStyle.None : DisplayStyle.Flex;
        RenameRequested?.Invoke(string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>Throws the edit away and puts the previous name back - Escape is not a decision to make.</summary>
    public void CancelEdit()
    {
        if (!_editing)
            return;

        EndEdit();
    }

    private void EndEdit()
    {
        _editing = false;
        _editor.style.display = DisplayStyle.None;
        _label.style.display = DisplayStyle.Flex;
    }

    private void OnKeyDown(KeyDownEvent evt)
    {
        if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
        {
            // The field is a one-line name, so Enter never reaches anything behind it.
            evt.StopPropagation();
            CommitEdit();
        }
        else if (evt.keyCode == KeyCode.Escape)
        {
            evt.StopPropagation();
            CancelEdit();
        }
    }
}

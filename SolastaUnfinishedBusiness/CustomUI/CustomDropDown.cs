using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;

namespace SolastaUnfinishedBusiness.CustomUI;

public class CustomDropDown : IDisposable
{
    public delegate void OnValueChanged(TMP_Dropdown.OptionData selected);

    public readonly GuiDropdown DropList;
    public readonly List<TMP_Dropdown.OptionData> Options = [];
    public readonly GuiGamepadSelector Selector;

    private bool _active = true;
    private bool _disposed;

    public OnValueChanged OnValueChangedHandler;

    public CustomDropDown(GuiDropdown dropList, GuiGamepadSelector selector)
    {
        DropList = dropList;
        Selector = selector;

        ClearOptions();

        dropList.onValueChanged.AddListener(OnDropdownValueChanged);
        selector.SelectionChanged += OnSelectorSelectionChanged;
    }

    public int Selected { get; private set; }

    public void SetActive(bool value)
    {
        if (_disposed)
        {
            return;
        }

        _active = value;
        UpdateControls();
    }

    public void UpdateControls()
    {
        if (_disposed || !DropList || !Selector)
        {
            return;
        }

        var gamepadActive = Gui.GamepadActive;

        DropList.gameObject.SetActive(_active && !gamepadActive);
        Selector.gameObject.SetActive(_active && gamepadActive);

        if (gamepadActive)
        {
            Selector.OnEnable();
        }
        else
        {
            Selector.OnDisable();
        }
    }

    public void ClearOptions()
    {
        if (_disposed)
        {
            return;
        }

        Selected = 0;
        Options.Clear();
        DropList.ClearOptions();
        Selector.Texts = [];
    }

    public void AddOptions(IEnumerable<TMP_Dropdown.OptionData> values)
    {
        if (_disposed)
        {
            return;
        }

        Options.AddRange(values);
        DropList.AddOptions(Options);
        Selector.Texts.AddRange(Options.Select(o => o.text));
        Selector.RefreshCurrent();
    }

    public void SetSelected(int newValue)
    {
        if (_disposed)
        {
            return;
        }

        Selected = newValue;
        DropList.SetValueWithoutNotify(newValue);
        Selector.currentSelection = newValue;
        NotifyValueChange();
    }

    public void Dispose()
    {
        Dispose(true);
    }

    public void Dispose(bool destroyGameObjects)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        OnValueChangedHandler = null;
        Options.Clear();

        if (DropList)
        {
            DropList.onValueChanged.RemoveListener(OnDropdownValueChanged);

            if (destroyGameObjects)
            {
                UnityEngine.Object.Destroy(DropList.gameObject);
            }
            else
            {
                DropList.gameObject.SetActive(false);
            }
        }

        if (!Selector)
        {
            return;
        }

        Selector.SelectionChanged -= OnSelectorSelectionChanged;
        Selector.Texts = [];

        if (destroyGameObjects)
        {
            UnityEngine.Object.Destroy(Selector.gameObject);
        }
        else
        {
            Selector.gameObject.SetActive(false);
        }
    }

    private void NotifyValueChange()
    {
        if (_disposed || Selected < 0 || Selected >= Options.Count)
        {
            return;
        }

        OnValueChangedHandler?.Invoke(Options[Selected]);
    }

    private void OnDropdownValueChanged(int newValue)
    {
        if (_disposed)
        {
            return;
        }

        Selected = newValue;
        Selector.currentSelection = newValue;
        NotifyValueChange();
    }

    private void OnSelectorSelectionChanged()
    {
        if (_disposed)
        {
            return;
        }

        Selected = Selector.currentSelection;
        DropList.SetValueWithoutNotify(Selected);
        NotifyValueChange();
    }

    internal static GuiDropdown MakeDropdown(string name, Transform transform)
    {
        // ReSharper disable once Unity.UnknownResource
        var gameObject = UnityEngine.Object.Instantiate(
            Resources.Load<GameObject>("GUI/Prefabs/Component/Dropdown"), transform);
        gameObject.name = name;
        return gameObject.GetComponent<GuiDropdown>();
    }


    internal static GuiGamepadSelector MakeSelector(string name, Transform transform)
    {
        // ReSharper disable once Unity.UnknownResource
        var gameObject =
            UnityEngine.Object.Instantiate(Resources.Load<GameObject>("Gui/Prefabs/Component/GamepadSelector"), transform);
        gameObject.name = name;
        var component = gameObject.GetComponent<GuiGamepadSelector>();
        component.actionMapName = "ModalListBrowse";
        component.actionName = "GamepadSelector";

        return component;
    }
}

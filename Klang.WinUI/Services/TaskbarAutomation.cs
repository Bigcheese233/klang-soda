using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Klang_WinUI.Services;

/// <summary>
/// Windows 原生 UI Automation COM 客户端。绑定由 CsWin32 在编译时生成，不依赖 WPF。
/// 所有 COM 对象均在同一个 MTA 查询线程创建、使用和释放；仅向外返回物理像素矩形。
/// https://learn.microsoft.com/windows/win32/winauto/uiauto-threading
/// </summary>
internal sealed class TaskbarAutomation : IDisposable
{
    private readonly List<object> _owned = new();
    private readonly IUIAutomation _automation;
    private readonly IUIAutomationCacheRequest _cache;
    private readonly IUIAutomationCondition _controls;

    public TaskbarAutomation()
    {
        try
        {
            _automation = Own((IUIAutomation)new CUIAutomation());
            _cache = Own(_automation.CreateCacheRequest());
            _cache.TreeScope = TreeScope.TreeScope_Element;
            _cache.AutomationElementMode = AutomationElementMode.AutomationElementMode_None;
            _cache.AddProperty(UIA_PROPERTY_ID.UIA_BoundingRectanglePropertyId);
            _cache.AddProperty(UIA_PROPERTY_ID.UIA_ControlTypePropertyId);
            _cache.AddProperty(UIA_PROPERTY_ID.UIA_IsOffscreenPropertyId);
            _cache.AddProperty(UIA_PROPERTY_ID.UIA_ProcessIdPropertyId);
            var types = new[]
            {
                UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId, UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId,
                UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId, UIA_CONTROLTYPE_ID.UIA_TextControlTypeId,
                UIA_CONTROLTYPE_ID.UIA_ImageControlTypeId, UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId,
                UIA_CONTROLTYPE_ID.UIA_CheckBoxControlTypeId, UIA_CONTROLTYPE_ID.UIA_RadioButtonControlTypeId,
                UIA_CONTROLTYPE_ID.UIA_ComboBoxControlTypeId, UIA_CONTROLTYPE_ID.UIA_EditControlTypeId,
                UIA_CONTROLTYPE_ID.UIA_HyperlinkControlTypeId, UIA_CONTROLTYPE_ID.UIA_SliderControlTypeId
            };
            var conditions = types.Select(type => Own(_automation.CreatePropertyCondition(
                UIA_PROPERTY_ID.UIA_ControlTypePropertyId, (int)type))).ToArray();
            _controls = Own(_automation.CreateOrConditionFromNativeArray(conditions, conditions.Length));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>只搜索指定任务栏的子树；未识别到有效任务按钮时返回 null，不把未知区域当空白。</summary>
    public IReadOnlyList<ScreenRect>? OccupiedBounds(IntPtr taskbar, ScreenRect bar, int excludedProcessId)
    {
        IUIAutomationElement? root = null;
        IUIAutomationElementArray? elements = null;
        try
        {
            root = _automation.ElementFromHandle(new HWND(taskbar));
            elements = root.FindAllBuildCache(TreeScope.TreeScope_Descendants, _controls, _cache);
            int count = elements.Length;
            if (count > 1024) return null;
            var occupied = new List<ScreenRect>();
            int recognized = 0;
            for (int i = 0; i < count; i++)
            {
                var element = elements.GetElement(i);
                try
                {
                    if (element.CachedProcessId == excludedProcessId || element.CachedIsOffscreen) continue;
                    var r = element.CachedBoundingRectangle;
                    var bounds = new ScreenRect(r.left, r.top, r.right, r.bottom);
                    if (bounds.Width <= 0 || bounds.Height <= 0 || !bounds.Intersects(bar)) continue;
                    var type = element.CachedControlType;
                    // 大型自定义容器不是实际按钮，不能据此判定整个任务栏被占满。
                    if (type == UIA_CONTROLTYPE_ID.UIA_CustomControlTypeId && bounds.Width >= bar.Width * 0.8) continue;
                    occupied.Add(bounds);
                    if (type is UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId
                        or UIA_CONTROLTYPE_ID.UIA_ListItemControlTypeId or UIA_CONTROLTYPE_ID.UIA_MenuItemControlTypeId)
                        recognized++;
                }
                finally { Release(element); }
            }
            return recognized == 0 ? null : occupied;
        }
        finally
        {
            Release(elements);
            Release(root);
        }
    }

    private T Own<T>(T value) where T : class { _owned.Add(value); return value; }
    private static void Release(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) Release(_owned[i]);
        _owned.Clear();
    }
}

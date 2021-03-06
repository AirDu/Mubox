﻿using Mubox.Configuration;
using Mubox.Control.Input.Hooks;
using Mubox.Model;
using Mubox.Model.Client;
using Mubox.Model.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;

// TODO the horrible naming conventions, such as "Mouse_AbsoluteMovement_Screen_Resolution_UpdateTimestampTicks" represent code that should be factored into domain objects.

namespace Mubox.View.Server
{
    /// <summary>
    /// Interaction logic for ServerWindow.xaml
    /// </summary>
    public partial class ServerWindow : Window
    {
        public static ServerWindow Instance { get; set; }

        internal double Mouse_RelativeMovement_LastX = double.MinValue;
        internal double Mouse_RelativeMovement_LastY = double.MinValue;

        internal long Mouse_AbsoluteMovement_Screen_Resolution_UpdateTimestampTicks;
        internal int Mouse_AbsoluteMovement_Screen_ResolutionX = WinAPI.SystemMetrics.GetSystemMetrics(WinAPI.SystemMetrics.SM.SM_CXSCREEN);
        internal int Mouse_AbsoluteMovement_Screen_ResolutionY = WinAPI.SystemMetrics.GetSystemMetrics(WinAPI.SystemMetrics.SM.SM_CYSCREEN);

        public ClientBase ActiveClient { get; private set; }

        public ServerWindow()
        {
            InitializeComponent();
            Mubox.Control.Network.Server.ClientAccepted += Server_ClientAccepted;
            Mubox.Control.Network.Server.ClientRemoved += Server_ClientRemoved;
            Keyboard_ActionMap = Keyboard_ActionMap_CreateNew((e) => e.Handled);
            Keyboard_ActionMap_SetDefaultActions(Keyboard_ActionMap);
        }

        private Point L_e_Point = new Point(0, 0);
        private ClientBase[] clientsCached = null;
        private int[/*vk*/] roundRobinTable = new int[ushort.MaxValue];
        private Dictionary<WinAPI.WM, ButtonState> Mouse_Buttons = InitializeMouseButtons();

        private static Dictionary<WinAPI.WM, ButtonState> InitializeMouseButtons()
        {
            Dictionary<WinAPI.WM, ButtonState> buttons = new Dictionary<WinAPI.WM, ButtonState>();
            foreach (var group in new WinAPI.WM[][]
                {
                    new WinAPI.WM[]
                    {
                        WinAPI.WM.LBUTTONDOWN,
                        WinAPI.WM.LBUTTONDBLCLK,
                        WinAPI.WM.LBUTTONUP,
                    },
                    new WinAPI.WM[]
                    {
                        WinAPI.WM.MBUTTONDOWN,
                        WinAPI.WM.MBUTTONDBLCLK,
                        WinAPI.WM.MBUTTONUP,
                    },
                    new WinAPI.WM[]
                    {
                        WinAPI.WM.RBUTTONDOWN,
                        WinAPI.WM.RBUTTONDBLCLK,
                        WinAPI.WM.RBUTTONUP,
                        WinAPI.WM.MOUSEMOVE, // for 'EnableMousePanningFix'
                    },
                    new WinAPI.WM[]
                    {
                        WinAPI.WM.XBUTTONDOWN,
                        WinAPI.WM.XBUTTONDBLCLK,
                        WinAPI.WM.XBUTTONUP
                    },
                })
            {
                var state = new ButtonState
                {
                    LastDownTimestamp = DateTime.MinValue,
                    LastUpTimestamp = DateTime.MinValue
                };
                foreach (var button in group)
                {
                    buttons[button] = state;
                }
            }
            return buttons;
        }

        private void MouseInputHook_MouseInputReceived(object sender, MouseInput e)
        {
            #region Mouse 'Button State' Tracking

            ButtonState mouseButtonInfo = null;
            if (Mouse_Buttons.TryGetValue(e.WM, out mouseButtonInfo))
            {
                var dateTimeNow = DateTime.Now;
                switch (e.WM)
                {
                    case WinAPI.WM.MOUSEMOVE:
                        if (mouseButtonInfo.IsDown && MuboxConfigSection.Default.Profiles.ActiveProfile.EnableMousePanningFix)
                        {
                            if ((int)e.Point.X != mouseButtonInfo.ClickX || (int)e.Point.Y != mouseButtonInfo.ClickY)
                            {
                                // TODO: screen bounds code may be incorrect for multimon, not properly tested
                                var drawingPoint = new System.Drawing.Point(mouseButtonInfo.ClickX, mouseButtonInfo.ClickY);
                                var screen = System.Windows.Forms.Screen.FromPoint(drawingPoint);
                                var x = (ushort)Math.Ceiling((double)(mouseButtonInfo.ClickX) * (65536.0 / (double)screen.Bounds.Width));
                                var y = (ushort)Math.Ceiling((double)(mouseButtonInfo.ClickY) * (65536.0 / (double)screen.Bounds.Height));
                                var mouseinput = new Mubox.WinAPI.SendInputApi.INPUT
                                {
                                    InputType = WinAPI.SendInputApi.InputType.INPUT_MOUSE,
                                    mi = new WinAPI.SendInputApi.MOUSEINPUT
                                    {
                                        dwExtraInfo = WinAPI.SendInputApi.GetMessageExtraInfo(),
                                        dx = x,
                                        dy = y,
                                        Flags = WinAPI.SendInputApi.MouseEventFlags.MOUSEEVENTF_MOVE | WinAPI.SendInputApi.MouseEventFlags.MOUSEEVENTF_ABSOLUTE,
                                        mouseData = 0,
                                        time = e.Time + 1,
                                    },
                                };
                                WinAPI.SendInputApi.SendInput(1, new Mubox.WinAPI.SendInputApi.INPUT[]
                                {
                                    mouseinput,
                                }, Marshal.SizeOf(mouseinput));
                                e.Handled = true;
                            }
                            else
                            {
                                e.Handled = true;
                            }
                            return;
                        }
                        break;

                    case WinAPI.WM.LBUTTONDOWN:
                    case WinAPI.WM.RBUTTONDOWN:
                    case WinAPI.WM.MBUTTONDOWN:
                    case WinAPI.WM.XBUTTONDOWN:
                        if (!mouseButtonInfo.IsDown)
                        {
                            mouseButtonInfo.ClickX = (int)e.Point.X;
                            mouseButtonInfo.ClickY = (int)e.Point.Y;
                            mouseButtonInfo.IsClick = false;
                            mouseButtonInfo.IsDoubleClick =
                                // within buffer timestamp for a second down/up transition to be interpretted as a double-click
                                (DateTime.Now.Ticks <= mouseButtonInfo.LastClickTimestamp.AddMilliseconds(Mubox.Configuration.MuboxConfigSection.Default.ClickBufferMilliseconds).Ticks)
                                // and last click event was not also a double-click event
                                && mouseButtonInfo.LastDoubleClickTimestamp != mouseButtonInfo.LastClickTimestamp;
                            if (mouseButtonInfo.IsDoubleClick)
                            {
                                // translate message
                                switch (e.WM)
                                {
                                    case WinAPI.WM.LBUTTONDOWN:
                                        e.WM = WinAPI.WM.LBUTTONDBLCLK;
                                        break;

                                    case WinAPI.WM.RBUTTONDOWN:
                                        e.WM = WinAPI.WM.RBUTTONDBLCLK;
                                        break;

                                    case WinAPI.WM.MBUTTONDOWN:
                                        e.WM = WinAPI.WM.MBUTTONDBLCLK;
                                        break;

                                    case WinAPI.WM.XBUTTONDOWN:
                                        e.WM = WinAPI.WM.XBUTTONDBLCLK;
                                        break;
                                }
                            }
                            mouseButtonInfo.LastDownTimestamp = dateTimeNow;
                        }
                        break;

                    case WinAPI.WM.LBUTTONDBLCLK:
                    case WinAPI.WM.MBUTTONDBLCLK:
                    case WinAPI.WM.RBUTTONDBLCLK:
                    case WinAPI.WM.XBUTTONDBLCLK:
                        mouseButtonInfo.IsDoubleClick = true;
                        break;

                    case WinAPI.WM.LBUTTONUP:
                    case WinAPI.WM.RBUTTONUP:
                    case WinAPI.WM.MBUTTONUP:
                    case WinAPI.WM.XBUTTONUP:
                        if (mouseButtonInfo.IsDown)
                        {
                            mouseButtonInfo.IsClick =
                                // within buffer timestamp for down/up transition to be interpretted as a 'click' gesture
                                dateTimeNow.Ticks <= mouseButtonInfo.LastDownTimestamp.AddMilliseconds(Mubox.Configuration.MuboxConfigSection.Default.ClickBufferMilliseconds).Ticks;
                            if (mouseButtonInfo.IsClick)
                            {
                                if (mouseButtonInfo.IsDoubleClick)
                                {
                                    mouseButtonInfo.IsDoubleClick = false;
                                    mouseButtonInfo.LastDoubleClickTimestamp = dateTimeNow;
                                }
                                mouseButtonInfo.IsClick = false;
                                mouseButtonInfo.LastClickTimestamp = dateTimeNow;
                            }
                            mouseButtonInfo.LastUpTimestamp = dateTimeNow;
                        }
                        break;
                }
                e.IsClickEvent = mouseButtonInfo.IsClick;
                e.IsDoubleClickEvent = mouseButtonInfo.IsDoubleClick;
            }

            #endregion Mouse 'Button State' Tracking

            if (!Mubox.Configuration.MuboxConfigSection.Default.EnableMouseCapture)
            {
                e.Handled = false;
                return;
            }

            Mubox.Extensions.ExtensionManager.Instance.OnMouseInputReceived(sender, e);

            ClientBase activeClient = ActiveClient;

            #region Mouse_RelativeMovement

            // one-time init
            if (e.WM == WinAPI.WM.MOUSEMOVE)
            {
                if (this.Mouse_RelativeMovement_LastX == double.MinValue)
                {
                    this.Mouse_RelativeMovement_LastX = e.Point.X;
                    this.Mouse_RelativeMovement_LastY = e.Point.Y;
                    return;
                }

                // update for resolution changes every XX seconds
                if ((DateTime.Now.Ticks - Mouse_AbsoluteMovement_Screen_Resolution_UpdateTimestampTicks) > TimeSpan.FromSeconds(15).Ticks)
                {
                    Mouse_Screen_OnResolutionChanged();
                    return;
                }
            }

            var shouldMulticastMouse = ShouldMulticastMouse(e);

            // track relative movement
            // TODO: except when the mouse moves TO the center of the screen or center of client area, some games do this to implement view pan
            // TODO: except when the mouse moves FROM the center of the screen, somce games do this to implement view pan
            int relX = (int)e.Point.X;
            int relY = (int)e.Point.Y;

            if (e.WM == WinAPI.WM.MOUSEMOVE)
            {
                relX -= (int)this.Mouse_RelativeMovement_LastX;
                relY -= (int)this.Mouse_RelativeMovement_LastY;

                this.Mouse_RelativeMovement_LastX += relX;
                if (this.Mouse_RelativeMovement_LastX <= 0)
                {
                    this.Mouse_RelativeMovement_LastX = 0;
                }
                else if (this.Mouse_RelativeMovement_LastX >= Mouse_AbsoluteMovement_Screen_ResolutionX)
                {
                    this.Mouse_RelativeMovement_LastX = Mouse_AbsoluteMovement_Screen_ResolutionX;
                }

                this.Mouse_RelativeMovement_LastY += relY;
                if (this.Mouse_RelativeMovement_LastY <= 0)
                {
                    this.Mouse_RelativeMovement_LastY = 0;
                }
                else if (this.Mouse_RelativeMovement_LastY >= Mouse_AbsoluteMovement_Screen_ResolutionY)
                {
                    this.Mouse_RelativeMovement_LastY = Mouse_AbsoluteMovement_Screen_ResolutionY;
                }
            }

            #endregion Mouse_RelativeMovement

            // send to client
            if (Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled && (activeClient != null))
            {
                //if (!shouldMulticastMouse)
                //{
                //    e.Handled = !(activeClient.IsLocalAddress && (e.WM == Win32.WM.MOUSEMOVE));
                //    if (!activeClient.IsLocalAddress || e.WM != Win32.WM.MOUSEMOVE)
                //    {
                //        e.Point = new Point(relX, relY);
                //        e.WindowStationHandle = activeClient.WindowStationHandle;
                //        e.WindowHandle = activeClient.WindowHandle;
                //        activeClient.Dispatch(e);
                //    }
                //}
                //else
                {
                    e.Handled = (e.WM != WinAPI.WM.MOUSEMOVE);

                    var clients = shouldMulticastMouse
                        ? GetCachedClients()
                        : new[] { activeClient };

                    if (e.WM == WinAPI.WM.MOUSEMOVE)
                    {
                        // track mouse position
                        TrackMousePositionClientRelative(e, activeClient, clients);

                        // in the case of the 'active, local' client it is presumed that the mouse is correctly interacting with said client and thus is removed from the list of target clients, otherwise the client may be receiving double the number of mousemove events
                        if (clients.Length > 0)
                        {
                            if (mouseButtonInfo.IsDown && MuboxConfigSection.Default.Profiles.ActiveProfile.EnableMousePanningFix)
                            {
                                clients = clients.Where(c => c.ClientId != activeClient.ClientId)
                                .ToArray();
                            }
                        }
                    }

                    foreach (ClientBase client in clients)
                    {
                        ForwardMouseEvent(e, client);
                    }
                }
            }
        }

        private void TrackMousePositionClientRelative(MouseInput e, ClientBase activeClient, ClientBase[] clients)
        {
            ClientBase clientForCoordinateNormalization = activeClient;
            if (clientForCoordinateNormalization == null || !clientForCoordinateNormalization.IsLocalAddress || !IsMouseInClientArea(e, GetScreenRelativeClientRect(clientForCoordinateNormalization)))
            {
                // look for a local client which the mouse coordinates "belong to" and re-assign "clientForCoordinateNormalization"
                clientForCoordinateNormalization = null;
                foreach (var item in clients)
                {
                    if (item.IsLocalAddress && IsMouseInClientArea(e, GetScreenRelativeClientRect(item)))
                    {
                        clientForCoordinateNormalization = item;
                        break;
                    }
                }
            }
            if (clientForCoordinateNormalization != null)
            {
                // track client-relative position
                this.L_e_Point = new Point(
                    Math.Ceiling((double)(e.Point.X - clientForCoordinateNormalization.CachedScreenFromClientRect.Left) * (65536.0 / (double)clientForCoordinateNormalization.CachedScreenFromClientRect.Width)),
                    Math.Ceiling((double)(e.Point.Y - clientForCoordinateNormalization.CachedScreenFromClientRect.Top) * (65536.0 / (double)clientForCoordinateNormalization.CachedScreenFromClientRect.Height)));
            }
        }

        private static bool IsMouseInClientArea(MouseInput e, WinAPI.Windows.RECT calcRect)
        {
            return (calcRect.Left <= (int)e.Point.X && calcRect.Top <= (int)e.Point.Y) && (calcRect.Right >= (int)e.Point.X && calcRect.Bottom >= (int)e.Point.Y);
        }

        private static WinAPI.Windows.RECT GetScreenRelativeClientRect(ClientBase item)
        {
            if (item.CachedScreenFromClientRectExpiry.Ticks < DateTime.Now.Ticks)
            {
                WinAPI.Windows.RECT calcRect;
                WinAPI.Windows.RECT clientRect;
                WinAPI.Windows.GetWindowRect(item.WindowHandle, out calcRect);
                WinAPI.Windows.GetClientRect(item.WindowHandle, out clientRect);
                int borderOffset = ((calcRect.Width - clientRect.Width) / 2);
                calcRect.Left += borderOffset;
                calcRect.Bottom -= borderOffset;
                calcRect.Top = calcRect.Bottom - clientRect.Height;
                item.CachedScreenFromClientRect = calcRect;
                item.CachedScreenFromClientRectExpiry = DateTime.Now.AddSeconds(3);
            }
            return item.CachedScreenFromClientRect;
        }

        private ClientBase[] GetCachedClients()
        {
            ClientBase[] result = clientsCached;
            if (result == null)
            {
                this.Dispatcher.Invoke((Action)delegate()
                {
                    clientsCached = ClientWindowProvider.ToArray();
                    result = clientsCached;
                });
            }
            return result;
        }

        private int thisCenterX;
        private int thisCenterWindowX;
        private int thisCenterClientAreaX;

        private int thisCenterY;
        private int thisCenterWindowY;
        private int thisCenterClientAreaY;

        private void Mouse_Screen_OnResolutionChanged()
        {
            Mouse_AbsoluteMovement_Screen_ResolutionX = WinAPI.SystemMetrics.GetSystemMetrics(WinAPI.SystemMetrics.SM.SM_CXSCREEN);
            Mouse_AbsoluteMovement_Screen_ResolutionY = WinAPI.SystemMetrics.GetSystemMetrics(WinAPI.SystemMetrics.SM.SM_CYSCREEN);
            // center server window on screen
            this.Dispatcher.BeginInvoke((Action)delegate()
            {
                double thisLeft = ((double)this.Mouse_AbsoluteMovement_Screen_ResolutionX - this.Width) / 2;
                double thisTop = ((double)this.Mouse_AbsoluteMovement_Screen_ResolutionY - this.Height) / 2;

                thisCenterX = Mouse_AbsoluteMovement_Screen_ResolutionX / 2;
                thisCenterWindowX = (int)this.Width / 2;
                thisCenterClientAreaX = thisCenterWindowX; // TODO: compensate for window border, if not hidden

                thisCenterY = Mouse_AbsoluteMovement_Screen_ResolutionY / 2;
                thisCenterWindowY = (int)this.Height / 2;
                thisCenterClientAreaY = thisCenterWindowX; // TODO: compensate for window border and title/caption, if not hidden

                if (this.Left != thisLeft || this.Top != thisTop)
                {
                    this.Left = thisLeft;
                    this.Top = thisTop;
                }
            });
            Mouse_AbsoluteMovement_Screen_Resolution_UpdateTimestampTicks = DateTime.Now.Ticks;
        }

        private static bool ShouldMulticastMouse(MouseInput e)
        {
            return
                (
                    ((Mubox.Configuration.MuboxConfigSection.Default.MouseMulticastMode == MouseMulticastModeType.Toggled) && WinAPI.IsToggled(WinAPI.VK.Capital))
                    ||
                    ((Mubox.Configuration.MuboxConfigSection.Default.MouseMulticastMode == MouseMulticastModeType.Pressed) && WinAPI.IsPressed(WinAPI.VK.Capital))
                );
        }

        private void ForwardMouseEvent(MouseInput e, ClientBase clientBase)
        {
            MouseInput L_e = new MouseInput();
            L_e.IsAbsolute = true;
            L_e.MouseData = e.MouseData;
            L_e.Point = L_e_Point;
            L_e.Time = e.Time;
            L_e.WindowDesktopHandle = clientBase.WindowDesktopHandle;
            L_e.WindowStationHandle = clientBase.WindowStationHandle;
            L_e.WindowHandle = clientBase.WindowHandle;
            L_e.WM = e.WM;
            clientBase.Dispatch(L_e);
        }

        private bool isSwitchingClients;

        public void CoerceActivation(IntPtr targetClientWindowHandle)
        {
            this.Dispatcher.Invoke((Action)delegate()
            {
                try
                {
                    for (int i = 0; i < listBox1.Items.Count; i++)
                    {
                        ClientBase client = (listBox1.Items[i] as ClientBase);
                        if (client.WindowHandle == targetClientWindowHandle)
                        {
                            listBox1.SelectedIndex = i;
                            ActivateSelectedClientAndRefreshView();
                        }
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            });
        }

        private void RefreshClientView()
        {
            CollectionViewSource view = this.FindResource("ClientListViewSource") as CollectionViewSource;
            if (view != null)
            {
                view.View.Filter = (item) =>
                    {
                        ClientBase client = item as ClientBase;
                        if (client == null)
                        {
                            return true;
                        }
                        if (client.ProfileName == MuboxConfigSection.Default.Profiles.ActiveProfile.Name)
                        {
                            return true;
                        }
                        return false;
                    };
                view.View.Refresh();
            }
        }

        public Dictionary<Mubox.WinAPI.VK, Dictionary<Mubox.WinAPI.WM, Func<KeyboardInput, bool>>> Keyboard_ActionMap { get; set; }

        private Func<KeyboardInput, bool> Keyboard_DefaultAction = null;

        private Dictionary<WinAPI.VK, Dictionary<Mubox.WinAPI.WM, Func<KeyboardInput, bool>>> Keyboard_ActionMap_CreateNew(Func<KeyboardInput, bool> keyboardAction)
        {
            Dictionary<WinAPI.VK, Dictionary<WinAPI.WM, Func<KeyboardInput, bool>>> actionMap = new Dictionary<WinAPI.VK, Dictionary<WinAPI.WM, Func<KeyboardInput, bool>>>();
            foreach (WinAPI.VK vk in Enum.GetValues(typeof(WinAPI.VK)))
            {
                actionMap[vk] = new Dictionary<WinAPI.WM, Func<KeyboardInput, bool>>();
            }

            // set default keyboard action
            this.Keyboard_DefaultAction = keyboardAction;

            return actionMap;
        }

        private void Keyboard_ActionMap_SetDefaultActions(Dictionary<WinAPI.VK, Dictionary<WinAPI.WM, Func<KeyboardInput, bool>>> actionMap)
        {
            // TODO: add visual indicator (need 'overlay ui' first)
            // TODO: audible indicator needs config setting to disable (may bother some users)
            // toggle input capture
            actionMap[WinAPI.VK.ScrollLock][WinAPI.WM.KEYDOWN] = (e) =>
            {
                RefreshClientView();
                WinAPI.Beep(
                    Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled
                        ? (uint)0x77FF
                        : (uint)0x7077,
                    (uint)200);
                return false;
            };
            actionMap[WinAPI.VK.ScrollLock][WinAPI.WM.KEYUP] = (e) =>
            {
                // toggle input capture, without client switcher
                SetInputCapture(!Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled, false);
                WinAPI.Beep(
                    Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled
                        ? (uint)0x77FF
                        : (uint)0x7077,
                    (uint)200);
                return false;
            };
            actionMap[WinAPI.VK.Pause][WinAPI.WM.KEYDOWN] = (e) =>
            {
                RefreshClientView();
                WinAPI.Beep(
                    Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled
                        ? (uint)0x77FF
                        : (uint)0x7077,
                    (uint)200);
                return false;
            };
            actionMap[WinAPI.VK.Pause][WinAPI.WM.KEYUP] = (e) =>
            {
                // toggle input capture, with client switcher
                SetInputCapture(!Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled, true);
                WinAPI.Beep(
                    Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled
                        ? (uint)0x77FF
                        : (uint)0x7077,
                    (uint)200);
                return false;
            };

            // accept switched-to client
            actionMap[WinAPI.VK.LeftMenu] = actionMap[WinAPI.VK.Menu];
            actionMap[WinAPI.VK.RightMenu] = actionMap[WinAPI.VK.Menu];
            actionMap[WinAPI.VK.Menu][WinAPI.WM.KEYUP] = (e) =>
            {
                if (Mubox.Configuration.MuboxConfigSection.Default.DisableAltTabHook)
                {
                    if (isSwitchingClients)
                    {
                        isSwitchingClients = false;
                    }
                    e.Handled = false;
                    return true;
                }
                if (isSwitchingClients && Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled)
                {
                    ClientBase activeClient = this.ActiveClient;

                    //ClientBase[] clients = null;
                    if (isSwitchingClients)
                    {
                        isSwitchingClients = false;

                        // deactivate any active clients
                        this.Dispatcher.BeginInvoke((Action)delegate()
                        {
                            ActivateSelectedClientAndRefreshView();
                        });
                    }
                }
                return false;
            };

            // replace windows task-switcher
            actionMap[WinAPI.VK.Tab][WinAPI.WM.SYSKEYDOWN] = (e) =>
            {
                if (Mubox.Configuration.MuboxConfigSection.Default.DisableAltTabHook)
                {
                    if (isSwitchingClients)
                    {
                        isSwitchingClients = false;
                    }
                    e.Handled = false;
                    return true;
                }
                if (Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled)
                {
                    e.Handled = true;
                    this.Dispatcher.BeginInvoke((Action)delegate()
                    {
                        if (!isSwitchingClients)
                        {
                            isSwitchingClients = true;
                            if (!this.IsVisible)
                            {
                                this.Show();
                                this.Topmost = true;
                                ("Attempted to Show Switcher").Log();
                            }
                        }
                        else
                        {
                            if (this.IsVisible)
                            {
                                this.Topmost = true;
                            }
                        }
                        bool reverseSelection = Mubox.Configuration.MuboxConfigSection.Default.ReverseClientSwitching ? !WinAPI.IsPressed(WinAPI.VK.Shift) : WinAPI.IsPressed(WinAPI.VK.Shift);
                        if (reverseSelection)
                        {
                            // reverse-selection
                            if (listBox1.SelectedIndex - 1 <= -1)
                            {
                                if (listBox1.Items.Count > 0)
                                {
                                    listBox1.SelectedIndex = listBox1.Items.Count - 1;
                                }
                            }
                            else
                            {
                                listBox1.SelectedIndex = listBox1.SelectedIndex - 1;
                            }
                        }
                        else
                        {
                            // forward-selection
                            if (listBox1.SelectedIndex + 1 >= listBox1.Items.Count)
                            {
                                if (listBox1.Items.Count > 0)
                                {
                                    listBox1.SelectedIndex = 0;
                                }
                            }
                            else
                            {
                                listBox1.SelectedIndex = listBox1.SelectedIndex + 1;
                            }
                        }
                    });
                }
                return false;
            };
            actionMap[WinAPI.VK.Tab][WinAPI.WM.SYSKEYUP] = (e) =>
            {
                return (Mubox.Configuration.MuboxConfigSection.Default.DisableAltTabHook)
                    ? false
                    : true;
            };

            actionMap[WinAPI.VK.Capital][WinAPI.WM.KEYDOWN] = (e) =>
            {
                // work around for mouse hook breakage, we noticed under W7 that the mouse hook would be periodically "lost" - this allows us to fix it using the Mouse Multicast hotkey "CAPS LOCK"
                MouseInputHook.Stop();
                MouseInputHook.Start();
                e.Handled = false;
                return true;
            };
            actionMap[WinAPI.VK.Capital][WinAPI.WM.KEYUP] = (e) =>
            {
                e.Handled = false;
                return true;
            };
        }

        private void ActivateSelectedClientAndRefreshView()
        {
            try
            {
                ClientBase client = listBox1.SelectedItem as ClientBase;
                ActiveClient = client;
                if (client != null)
                {
                    client.Activate();
                    RefreshClientView();
                }
                else
                {
                    this.Hide();
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private void KeyboardInputHook_KeyboardInputReceived(object sender, KeyboardInput e)
        {
            if (this.Keyboard_DefaultAction != null)
            {
                if (this.Keyboard_DefaultAction(e) || e.Handled)
                {
                    return;
                }
            }

            Func<KeyboardInput, bool> action = null;
            var keyAction = Keyboard_ActionMap[(WinAPI.VK)e.VK];
            if (keyAction != null)
            {
                if (keyAction.TryGetValue(e.WM, out action))
                {
                    if (action(e) || e.Handled)
                    {
                        return;
                    }
                }
                //else
                //{
                //    Console.WriteLine("???? MISSING WM ACTION IN MESSAGE MAP - vk=" + (WinAPI.VK)e.VK + " wm=" + e.WM);
                //}
            }
            else
            {
                Console.WriteLine("???? MISSING KEY ACTION IN KEY MAP - vk=" + (WinAPI.VK)e.VK + " wm=" + e.WM);
            }

            if (!Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled)
            {
                return;
            }

            Mubox.Extensions.ExtensionManager.Instance.OnKeyboardInputReceived(sender, e);

            ClientBase activeClient = this.ActiveClient;

            if (Mubox.Configuration.MuboxConfigSection.Default.Profiles.ActiveProfile.EnableMulticast && !ActiveClientOnly(e))
            {
                e.Handled = true;

                ClientBase[] clients = GetCachedClients();

                KeySetting globalKeySetting;
                if (MuboxConfigSection.Default.Profiles.ActiveProfile.Keys.TryGetKeySetting((WinAPI.VK)e.VK, out globalKeySetting))
                {
                    if (globalKeySetting.RoundRobinKey)
                    {
                        if (clients.Length > 0)
                        {
                            int clientIndex = roundRobinTable[(int)e.VK];
                            if (e.Flags.HasFlag(WinAPI.WindowHook.LLKHF.UP))
                            {
                                // we only update round-robin target on key-up, otherwise two different clients receive keydown vs. keyup events
                                roundRobinTable[(int)e.VK] = (byte)clientIndex + 1;
                            }
                            ClientBase client = clients[clientIndex % clients.Length];
                            InputToClient(e, client);
                            return;
                        }
                    }
                }

                foreach (ClientBase client in clients)
                {
                    InputToClient(e, client);
                }
            }
            else if (activeClient != null)
            {
                e.Handled = true;
                ForwardEventToClient(e, activeClient);
            }
        }

        private static void InputToClient(KeyboardInput e, ClientBase client)
        {
            // this method basically applies CAS settings and calls ForwardEventToClient
            byte cas = 0;
            var clientSettings = Mubox.Configuration.MuboxConfigSection.Default.Profiles.ActiveProfile.ActiveClient;
            if (clientSettings != null)
            {
                if (clientSettings.Name.Equals(client.DisplayName, StringComparison.InvariantCultureIgnoreCase))
                {
                    Mubox.Configuration.KeySetting keySetting;
                    if (clientSettings.Keys.TryGetKeySetting((WinAPI.VK)e.VK, out keySetting) && keySetting.EnableNoModActiveClient)
                    {
                        cas = (byte)e.CAS;
                        e.CAS = (WinAPI.CAS)0;
                    }
                }
            }
            ForwardEventToClient(e, client);
            if (cas != 0)
            {
                e.CAS = (WinAPI.CAS)cas;
            }
        }

        private static bool ActiveClientOnly(KeyboardInput e)
        {
            // TODO optimize !!! iterating 'all keys' looking for a match is a waste of CPU, perhaps a bitmap of Win32.VK makes sense, but it needs to be synchronized against the config (perhaps on Save)
            bool activeClientOnly = false;
            foreach (var item in Mubox.Configuration.MuboxConfigSection.Default.Profiles.ActiveProfile.Keys.Cast<Mubox.Configuration.KeySetting>())
            {
                if (item.ActiveClientOnly && (WinAPI.VK)e.VK == item.InputKey)
                {
                    activeClientOnly = true;
                    break;
                }
            }
            return activeClientOnly;
        }

        private static void ForwardEventToClient(KeyboardInput e, ClientBase clientBase)
        {
            try
            {
                e.WindowStationHandle = clientBase.WindowStationHandle;
                e.WindowDesktopHandle = clientBase.WindowDesktopHandle;
                e.WindowHandle = clientBase.WindowHandle;
                clientBase.Dispatch(e);
            }
            catch (Exception ex)
            {
                Mubox.Control.Network.Server.RemoveClient(clientBase);
                ex.Log();
            }
        }

        public void SetInputCapture(bool enable, bool showServerUI)
        {
            if (enable)
            {
                // TryAutoEnableMulticastOnce();
                if (!Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled)
                {
                    Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled = true;
                    HideServerUI();
                }
            }
            else
            {
                if (Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled)
                {
                    Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled = false;
                    if (showServerUI)
                    {
                        ShowServerUI();
                    }
                }
            }
        }

        private void ShowServerUI()
        {
            this.Dispatcher.Invoke((Action)delegate()
            {
                this.Show();
                this.Topmost = true;
            });
        }

        private void HideServerUI()
        {
            this.Dispatcher.Invoke((Action)delegate()
            {
                try
                {
                    ActiveClient = listBox1.SelectedItem as ClientBase;
                    if (ActiveClient != null)
                    {
                        ActiveClient.Activate();
                    }
                    else
                    {
                        this.Hide();
                    }
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            });
        }

        public void ToggleInputCapture(bool showServerUI)
        {
            SetInputCapture(!Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled, showServerUI);
        }

        #region ClientWindowProvider

        /// <summary>
        /// ClientWindowProvider Dependency Property
        /// </summary>
        public static readonly DependencyProperty ClientWindowProviderProperty =
            DependencyProperty.Register("ClientWindowProvider", typeof(ObservableCollection<ClientBase>), typeof(ServerWindow),
                new FrameworkPropertyMetadata((ObservableCollection<ClientBase>)new ObservableCollection<ClientBase>()));

        /// <summary>
        /// Gets or sets the ClientWindowProvider property.  This dependency property
        /// indicates a collection of ClientBase objects visible in the UI.
        /// </summary>
        public ObservableCollection<ClientBase> ClientWindowProvider
        {
            get { return (ObservableCollection<ClientBase>)GetValue(ClientWindowProviderProperty); }
            set { SetValue(ClientWindowProviderProperty, value); }
        }

        #endregion ClientWindowProvider

        private void Server_ClientAccepted(object sender, Mubox.Control.Network.Server.ServerEventArgs e)
        {
            this.Dispatcher.BeginInvoke((Action<Mubox.Control.Network.Server.ServerEventArgs>)delegate(Mubox.Control.Network.Server.ServerEventArgs args)
            {
                (args.Client as NetworkClient).ClientActivated += ServerWindow_ClientActivated;
                ClientWindowProvider.Add(args.Client);
            }, e);
            if (e.Client is NetworkClient)
            {
                (e.Client as NetworkClient).CoerceActivation += new EventHandler<EventArgs>(ServerWindow_CoerceActivation);
            }
            e.Client.IsAttachedChanged += Client_IsAttachedChanged;
            clientsCached = null;
        }

        private void ServerWindow_ClientActivated(object sender, EventArgs e)
        {
            this.Dispatcher.BeginInvoke((Action)delegate()
            {
                // set "ActiveClient" of MuboxConfig, primarily for CAS Modifiers feature
                NetworkClient networkClient = sender as NetworkClient;
                if (networkClient != null && !string.IsNullOrEmpty(networkClient.DisplayName))
                {
                    foreach (var L_profile in Mubox.Configuration.MuboxConfigSection.Default.Profiles.Cast<ProfileSettings>())
                    {
                        if (L_profile.Name == networkClient.ProfileName)
                        {
                            Mubox.Configuration.ClientSettings settings = L_profile.Clients.GetExisting(networkClient.DisplayName);
                            if (settings != null)
                            {
                                L_profile.ActiveClient = settings;
                                Mubox.Configuration.MuboxConfigSection.Default.Profiles.ActiveProfile = L_profile;
                                break;
                            }
                        }
                    }
                }
                this.Hide();
            });
        }

        private void Server_ClientRemoved(object sender, Mubox.Control.Network.Server.ServerEventArgs e)
        {
            this.Dispatcher.BeginInvoke((Action<Mubox.Control.Network.Server.ServerEventArgs>)delegate(Mubox.Control.Network.Server.ServerEventArgs args)
            {
                ClientWindowProvider.Remove(args.Client);
                RefreshClientView();
            }, e);
            if (e.Client is NetworkClient)
            {
                (e.Client as NetworkClient).CoerceActivation -= ServerWindow_CoerceActivation;
            }
            e.Client.IsAttachedChanged -= Client_IsAttachedChanged;
            clientsCached = null;
        }

        private void ServerWindow_CoerceActivation(object sender, EventArgs e)
        {
            this.CoerceActivation((sender as ClientBase).WindowHandle);
        }

        private void Client_IsAttachedChanged(object sender, EventArgs e)
        {
            ClientBase client = sender as ClientBase;
            if (client != null)
            {
                if (!client.IsAttached)
                {
                    this.Dispatcher.BeginInvoke((Action)delegate()
                    {
                        this.ClientWindowProvider.Remove(client);
                    });
                    if (client is NetworkClient)
                    {
                        (client as NetworkClient).CoerceActivation -= ServerWindow_CoerceActivation;
                    }
                }
            }
            clientsCached = null;
        }

        private void buttonStartServer_Click(object sender, RoutedEventArgs e)
        {
            string[] portNumberList = textListenPort.Text.Trim().Split(',');
            if ((string)buttonStartServer.Content == "Start")
            {
                buttonStartServer.Content = "Stop";
                try
                {
                    int portNumber = int.Parse(portNumberList[0]);
                    Mubox.Control.Network.Server.Start(portNumber);
                    textListenPort.IsEnabled = false;
                }
                catch (Exception ex)
                {
                    try
                    {
                        Window connectionDialog = Activator.CreateInstance(portNumberList[0], portNumberList[1]).Unwrap() as Window;
                        connectionDialog.Tag = portNumberList;
                        connectionDialog.Show();
                        textListenPort.IsEnabled = false;
                    }
                    catch
                    {
                        MessageBox.Show("Bad Port Number: " + ex.Message, "Mubox Server", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else
            {
                buttonStartServer.Content = "Start";
                Mubox.Control.Network.Server.Stop();
                textListenPort.IsEnabled = true;
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            KeyboardInputHook.KeyboardInputReceived -= KeyboardInputHook_KeyboardInputReceived;
            MouseInputHook.MouseInputReceived -= MouseInputHook_MouseInputReceived;

            KeyboardInputHook.Stop();
            MouseInputHook.Stop();

            Mubox.Control.Network.Server.Stop();

            ClientBase[] clients = GetCachedClients();
            this.Dispatcher.Invoke((Action)delegate()
            {
                ClientWindowProvider.Clear();
            });

            foreach (ClientBase client in clients)
            {
                NetworkClient networkClient = client as NetworkClient;
                if (networkClient != null)
                {
                    try
                    {
                        networkClient.Detach();
                    }
                    catch (Exception ex)
                    {
                        ex.Log();
                    }
                }
            }

            base.OnClosing(e);
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);

            InitializeThemes();
        }

        private void InitializeThemes()
        {
            List<Mubox.View.Themes.ThemeDescriptor> themesList = new List<Mubox.View.Themes.ThemeDescriptor>();
            AddThemeResource(themesList, "Default");
            AddThemeResource(themesList, "Black");
            AddThemeResource(themesList, "Orange");
            AddThemeResource(themesList, "Brown");
            AddThemeResource(themesList, "Red");
            AddThemeResource(themesList, "Green");
            AddThemeResource(themesList, "Blue");
            comboThemeSelector.SelectionChanged += (L_sender, L_e) =>
            {
                Mubox.View.Themes.ThemeDescriptor theme = (comboThemeSelector.SelectedItem as Mubox.View.Themes.ThemeDescriptor);
                if (Application.Current.Resources.MergedDictionaries.Count > 0)
                {
                    Application.Current.Resources.MergedDictionaries.Clear();
                }
                if (theme != null)
                {
                    Application.Current.Resources.MergedDictionaries.Add(theme.Resources);
                    Mubox.Configuration.MuboxConfigSection.Default.PreferredTheme = theme.Name;
                }
                else
                {
                    Mubox.Configuration.MuboxConfigSection.Default.PreferredTheme = "Default";
                }
                Mubox.Configuration.MuboxConfigSection.Save();
            };
            comboThemeSelector.ItemsSource = themesList;
            comboThemeSelector.DisplayMemberPath = "Name";
        }

        private void AddThemeResource(List<Mubox.View.Themes.ThemeDescriptor> themesList, string themeName)
        {
            try
            {
                var uri = new Uri("pack://siteoforigin:,,,/View/Themes/" + themeName + ".xaml", UriKind.Absolute);
                var resourceInfo = Application.GetRemoteStream(uri);
                var reader = new System.Windows.Markup.XamlReader();
                ResourceDictionary resourceDictionary = (ResourceDictionary)reader.LoadAsync(resourceInfo.Stream);
                Mubox.View.Themes.ThemeDescriptor theme = new Mubox.View.Themes.ThemeDescriptor
                {
                    Name = themeName,
                    Resources = resourceDictionary
                };
                themesList.Add(theme);
                if (Mubox.Configuration.MuboxConfigSection.Default.PreferredTheme == themeName)
                {
                    Application.Current.Resources.MergedDictionaries.Add(theme.Resources);
                    comboThemeSelector.SelectedItem = theme;
                }
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private bool isStartedYet;

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            this.Hide();
            this.Dispatcher.BeginInvoke((Action)delegate()
            {
                try
                {
                    if (isStartedYet)
                        return;
                    isStartedYet = true;

                    KeyboardInputHook.KeyboardInputReceived += KeyboardInputHook_KeyboardInputReceived;
                    MouseInputHook.MouseInputReceived += MouseInputHook_MouseInputReceived;
                    KeyboardInputHook.Start();
                    MouseInputHook.Start();

                    this.buttonStartServer_Click(null, null);

                    SetInputCapture(true, false);
                }
                catch (Exception ex)
                {
                    ex.Log();
                }
            });
        }

        private void buttonExit_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                this.Close();
            }
            catch (Exception ex)
            {
                ex.Log();
            }
        }

        private void buttonConfigureMulticast_Click(object sender, RoutedEventArgs e)
        {
            MulticastConfigDialog.ShowStaticDialog();
        }

        private void listBox1_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!Mubox.Configuration.MuboxConfigSection.Default.IsCaptureEnabled)
            {
                ToggleInputCapture(true);
            }
        }

        private void textListenPort_TextChanged(object sender, TextChangedEventArgs e)
        {
            // TODO persist to registry
        }
    }
}
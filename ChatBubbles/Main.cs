﻿using Dalamud.Configuration;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Dalamud.Data;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Gui;
using Dalamud.Logging;
using Num = System.Numerics;
using Lumina.Excel.GeneratedSheets;

namespace ChatBubbles
{
    public unsafe partial class ChatBubbles : IDalamudPlugin
    {
        public string Name => "Chat Bubbles";
        private readonly DalamudPluginInterface _pluginInterface;
        private readonly Config _configuration;
        private readonly CommandManager _commandManager;
        private readonly ClientState _clientState;

        private readonly ChatGui _chatGui;
        private readonly ObjectTable _objectTable;

        private bool _picker;
        private readonly List<CharData> _charDatas = new();
        private int _timer;
        private UiColorPick _chooser;
        private int _queue;
        private bool _stack;
        private bool _hide;

#if DEBUG
        private bool _config = true;
        private bool _debug = true;
#else
        private bool _config = false;
        private bool _debug = false;
#endif

        private readonly List<XivChatType> _channels;

        private readonly List<XivChatType> _order = new()
        {
            XivChatType.None, XivChatType.None, XivChatType.None, XivChatType.None, XivChatType.Say,
            XivChatType.Shout, XivChatType.TellOutgoing, XivChatType.TellIncoming, XivChatType.Party, XivChatType.Alliance,
            XivChatType.Ls1, XivChatType.Ls2, XivChatType.Ls3, XivChatType.Ls4, XivChatType.Ls5,
            XivChatType.Ls6, XivChatType.Ls7, XivChatType.Ls8, XivChatType.FreeCompany, XivChatType.NoviceNetwork,
            XivChatType.CustomEmote, XivChatType.StandardEmote, XivChatType.Yell, XivChatType.CrossParty, XivChatType.PvPTeam,
            XivChatType.CrossLinkShell1, XivChatType.None, XivChatType.None, XivChatType.None, XivChatType.None,
            XivChatType.None, XivChatType.None, XivChatType.CrossLinkShell2, XivChatType.CrossLinkShell3, XivChatType.CrossLinkShell4,
            XivChatType.CrossLinkShell5, XivChatType.CrossLinkShell6, XivChatType.CrossLinkShell7, XivChatType.CrossLinkShell8
        };

        private readonly bool[] _yesno = {
            false, false, false, false, true,
            true, true, true, true, true,
            true, true, true, true, true,
            true, true, true, true, true,
            true, true, true, true, true,
            true, false, false, false, false,
            false, false, true, true, true,
            true, true, true, true
        };


        private readonly UiColorPick[] _textColour;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr UpdateBubble(SeBubble* bubble, IntPtr actor, IntPtr dunnoA, IntPtr dunnoB);

        //private UpdateBubble _updateBubbleFunc;
        private readonly Hook<UpdateBubble> _updateBubbleFuncHook;
        //private IntPtr _updateBubblePtr;

        [UnmanagedFunctionPointer(CallingConvention.ThisCall, CharSet = CharSet.Ansi)]
        private delegate IntPtr OpenBubble(IntPtr self, IntPtr actor, IntPtr textPtr, bool notSure);

        //private OpenBubble _openBubbleFunc;
        private readonly Hook<OpenBubble> _openBubbleFuncHook;
        //private IntPtr _openBubblePtr;

        private readonly Lumina.Excel.ExcelSheet<UIColor> _uiColours;

        public ChatBubbles(
            DalamudPluginInterface pluginInterface,
            CommandManager commandManager,
            ClientState clientState,
            ChatGui chatGui,
            DataManager dataManager,
            ObjectTable objectTable,
            Dalamud.Game.SigScanner sigScannerD
            )
        {
            _pluginInterface = pluginInterface;
            _commandManager = commandManager;
            _clientState = clientState;
            _chatGui = chatGui;
            _objectTable = objectTable;
            var sigScanner = sigScannerD;
            
            _uiColours = dataManager.Excel.GetSheet<UIColor>();
            _configuration = pluginInterface.GetPluginConfig() as Config ?? new Config();
            _timer = _configuration.Timer;
            _channels = _configuration.Channels;
            _textColour = _configuration.TextColour;
            _queue = _configuration.Queue;
            _stack = _configuration.Stack;

            _chatGui.ChatMessage += Chat_OnChatMessage;
            _pluginInterface.UiBuilder.Draw += BubbleConfigUi;
            _pluginInterface.UiBuilder.OpenConfigUi += BubbleConfig;
            _commandManager.AddHandler("/bub", new CommandInfo(Command)
            {
                HelpMessage = "Opens the Chat Bubble config menu"
            });
            
            var updateBubblePtr = sigScanner.ScanText("48 85 D2 0F 84 ?? ?? ?? ?? 48 89 5C 24 ?? 57 48 83 EC 20 8B 41 0C");
            UpdateBubble updateBubbleFunc = UpdateBubbleFuncFunc;
            try
            {
                _updateBubbleFuncHook = new Hook<UpdateBubble>(updateBubblePtr + 0x9, updateBubbleFunc);
                _updateBubbleFuncHook.Enable();
                if (_debug) PluginLog.Log("GOOD");
            }
            catch (Exception e)
            { PluginLog.Log("BAD\n" + e); }

            var openBubblePtr = sigScanner.ScanText("E8 ?? ?? ?? ?? 80 BF ?? ?? ?? ?? ?? C7 07 ?? ?? ?? ??");
            OpenBubble openBubbleFunc = OpenBubbleFuncFunc;
            try
            {
                _openBubbleFuncHook = new Hook<OpenBubble>(openBubblePtr, openBubbleFunc);
                _openBubbleFuncHook.Enable();
                if (_debug) PluginLog.Log("GOOD2");
            }
            catch (Exception e)
            { PluginLog.Log("BAD\n" + e); }
        }
        

        private void SaveConfig()
        {
            _configuration.Timer = _timer;
            _configuration.Channels = _channels;
            _configuration.TextColour = _textColour;
            _configuration.Queue = _queue;
            _configuration.Stack = _stack;
            _configuration.Hide = _hide;
            _pluginInterface.SavePluginConfig(_configuration);
        }

        private IntPtr UpdateBubbleFuncFunc(SeBubble* bubble, IntPtr actor, IntPtr dunnoA, IntPtr dunnoB)
        {
            const int idOffset = 116;
            var actorId = Marshal.ReadInt32(actor + idOffset);

            foreach (var cd in _charDatas.Where(cd => actorId == cd.ActorId))
            {
                if (bubble->Status == SeBubbleStatus.Off)
                {
                    if (_debug)
                    {
                        PluginLog.Log("Switch On");
                        PluginLog.Log($"ActorID: {cd.ActorId}");
                    }

                    bubble->Status = SeBubbleStatus.Init;
                    bubble->Timer = _timer;
                }

                if (bubble->Status == SeBubbleStatus.On && cd.Stack)
                {
                    bubble->Status = SeBubbleStatus.Off;
                    bubble->Timer = 0;
                    cd.Stack = false;
                }
                break;
            }

            return _updateBubbleFuncHook.Original(bubble, actor, dunnoA, dunnoB);
        }

        private IntPtr OpenBubbleFuncFunc(IntPtr self, IntPtr actor, IntPtr textPtr, bool notSure)
        {
            const int idOffset = 116;
            var actorId = Marshal.ReadInt32(actor, idOffset);

            foreach (var cd in _charDatas.Where(cd => actorId == cd.ActorId))
            {
                if (_debug)
                {
                    PluginLog.Log("Update ballon text");
                    PluginLog.Log(cd.Message.TextValue);
                }

                if (cd.Message.TextValue.Length > 0)
                {
                    var bytes = cd.Message.Encode();
                    var newPointer = Marshal.AllocHGlobal(bytes.Length + 1);
                    Marshal.Copy(bytes, 0, newPointer, bytes.Length);
                    Marshal.WriteByte(newPointer, bytes.Length, 0);
                    textPtr = newPointer;
                }

                break;
            }

            return _openBubbleFuncHook.Original(self, actor, textPtr, notSure);
        }

        public void Dispose()
        {
            _chatGui.ChatMessage -= Chat_OnChatMessage;
            _pluginInterface.UiBuilder.Draw -= BubbleConfigUi;
            _pluginInterface.UiBuilder.OpenConfigUi -= BubbleConfig;
            _commandManager.RemoveHandler("/bub");
            _updateBubbleFuncHook.Disable();
            _openBubbleFuncHook.Disable();
        }

        private void BubbleConfig() => _config = true;


        // What to do when command is called
        private void Command(string command, string arguments) => _config = true;
        
 

        private uint GetActorId(string nameInput)
        {
            if (_hide && nameInput == _clientState.LocalPlayer.Name.TextValue) return 0;

            foreach (var t in _objectTable)
            {
                if (!(t is PlayerCharacter pc)) continue;
                if (pc.Name.TextValue == nameInput) return pc.ObjectId;
            }
            return 0;
        }

        private enum SeBubbleStatus : uint
        {
            GetData = 0,
            On = 1,
            Init = 2,
            Off = 3
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x80)]
        private struct SeBubble
        {
            [FieldOffset(0x0)] private readonly uint Id;
            [FieldOffset(0x4)] public float Timer;
            [FieldOffset(0x8)] private readonly uint Unk_8; // enum probably
            [FieldOffset(0xC)] public SeBubbleStatus Status; // state of the bubble
            [FieldOffset(0x10)] private readonly ulong Text;
            [FieldOffset(0x78)] private readonly ulong Unk_78; // check whats in memory here
        }

        private class CharData
        {
            public SeString Message;
            public uint ActorId;
            public DateTime MessageDateTime;
            public string Name;
            public bool Stack { get; set; }
        }
    }

    public class UiColorPick
    {
        public uint Choice { get; set; }
        public uint Option { get; set; }
    }

    public class Config : IPluginConfiguration
    {
        public int Version { get; set; } = 0;
        public List<XivChatType> Channels { get; set; } = new() {XivChatType.Say};
        public int Timer { get; set; } = 7;
        public bool Stack { get; set; }
        public bool Hide { get; set; }

        public UiColorPick[] TextColour { get; set; } =
        {
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 },
            new() { Choice = 0, Option =0 }, new() { Choice = 0, Option =0 }, new () { Choice = 0, Option =0 }
        };

        public int Queue { get; set; } = 3;
    }
}
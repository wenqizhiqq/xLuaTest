using ScintillaNET;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using XLua;

namespace LuaEditor.LuaDebug
{
    /// <summary>
    /// 独立XLua Lua调试核心，无WinForm窗口依赖
    /// 支持：断点、StepInto/StepOver/Continue、执行行高亮、多线程阻塞调试
    /// 兼容C#7.3 传统using()写法，无using var语法
    /// </summary>
    public class LuaSingleDebuggerXLua : IDisposable
    {
        #region 常量定义
        private const int MARK_EXECUTED = 1;
        private const int MARK_CURRENT_LINE = 2;
        private const int MARK_BREAKPOINT = 3;
        private const int LUA_LINE_OFFSET = 1;
        #endregion

        #region 调试状态枚举
        public enum DebugRunState
        {
            Run,
            StepInto,
            StepOver,
            Paused
        }
        #endregion

        #region 私有字段
        private readonly Scintilla _editor;
        private LuaEnv _luaEnv;
        private bool _disposed;

        public readonly HashSet<int> BreakPoints = new HashSet<int>();
        private readonly HashSet<int> _executedLines = new HashSet<int>();
        private readonly Queue<int> _renderLineQueue = new Queue<int>();
        private readonly ManualResetEvent _debugPauseSignal = new ManualResetEvent(true);

        public DebugRunState CurrentState { get; private set; } = DebugRunState.Run;
        private DebugRunState _pendingStepMode = DebugRunState.Run;
        private int _stepOverStackBaseDepth;
        private int _lastRenderCurrentLine = -1;

        [CSharpCallLua]
        public delegate void DbgLineCallback(int luaLine);
        private DbgLineCallback _onLineExecute;

        [CSharpCallLua]
        public delegate void DbgBlockPause(int luaLine);
        private DbgBlockPause _blockScriptWait;

        [CSharpCallLua]
        public delegate int DbgQueryDebugMode(int stackDepth, int baseDepth);
        private DbgQueryDebugMode _queryDebugMode;
        #endregion

        #region 构造与初始化
        public LuaSingleDebuggerXLua(Scintilla editor)
        {
            _editor = editor ?? throw new ArgumentNullException(nameof(editor));

            _onLineExecute = OnLuaLineExecuted;
            _blockScriptWait = BlockLuaThreadPause;
            _queryDebugMode = GetActiveDebugMode;

            _editor.DoubleClick += Editor_DoubleClickBreakpoint;
            InitEditorMarkerStyle();
        }

        private void InitEditorMarkerStyle()
        {
            _editor.Markers[MARK_EXECUTED].SetBackColor(Color.LightGreen);
            _editor.Markers[MARK_EXECUTED].Symbol = MarkerSymbol.LeftRect;

            _editor.Markers[MARK_CURRENT_LINE].SetBackColor(Color.Yellow);
            _editor.Markers[MARK_CURRENT_LINE].Symbol = MarkerSymbol.RoundRect;

            _editor.Markers[MARK_BREAKPOINT].SetBackColor(Color.Red);
            _editor.Markers[MARK_BREAKPOINT].Symbol = MarkerSymbol.Circle;

            _editor.Margins[0].Width = 40;
            _editor.Margins[0].Type = MarginType.Number;

            _editor.Lexer = Lexer.Lua;
            _editor.StyleResetDefault();
            _editor.Styles[Style.Default].Font = "Consolas";
            _editor.Styles[Style.Default].Size = 10;
        }
        #endregion

        #region 断点管理
        private void Editor_DoubleClickBreakpoint(object sender, EventArgs e)
        {
            int clickPos = _editor.CurrentPosition;
            int clickLine = _editor.LineFromPosition(clickPos);
            ToggleBreakpoint(clickLine);
        }

        public void ToggleBreakpoint(int editorLine)
        {
            if (editorLine < 0 || editorLine >= _editor.Lines.Count)
                return;

            int luaLine = editorLine + LUA_LINE_OFFSET;
            if (BreakPoints.Contains(editorLine))
            {
                BreakPoints.Remove(editorLine);
                _editor.Lines[editorLine].MarkerDelete(MARK_BREAKPOINT);
                SyncSingleBreakpointToLua(luaLine, null);
            }
            else
            {
                BreakPoints.Add(editorLine);
                _editor.Lines[editorLine].MarkerAdd(MARK_BREAKPOINT);
                SyncSingleBreakpointToLua(luaLine, true);
            }
        }

        private void SyncSingleBreakpointToLua(int luaLine, object value)
        {
            if (_luaEnv == null) return;
            using (LuaTable breakTbl = _luaEnv.Global.Get<LuaTable>("BreakPointLine"))
            {
                breakTbl[luaLine] = value;
            }
        }

        private void SyncAllBreakpointsToLua()
        {
            if (_luaEnv == null) return;
            using (LuaTable breakTbl = _luaEnv.Global.Get<LuaTable>("BreakPointLine"))
            {
                // 替换 breakTbl.Keys，使用原生GetKeys获取object数组
                object[] allKeys = (object[])breakTbl.GetKeys();
                foreach (var key in allKeys)
                {
                    breakTbl[key] = null;
                }

                foreach (int editorLine in BreakPoints)
                {
                    breakTbl[editorLine + LUA_LINE_OFFSET] = true;
                }
            }
        }
        #endregion

        #region 调试钩子挂载卸载
        public void AttachDebugHook(LuaEnv luaEnv)
        {
            if (luaEnv == null || _luaEnv == luaEnv)
                return;

            DetachDebugHook();
            _luaEnv = luaEnv;

            _luaEnv.Global.Set("dbg_line_notify", _onLineExecute);
            _luaEnv.Global.Set("dbg_block_wait", _blockScriptWait);
            _luaEnv.Global.Set("dbg_get_mode", _queryDebugMode);

            _luaEnv.DoString("BreakPointLine = {}");
            SyncAllBreakpointsToLua();

            string hookCoreScript = @"
local lastExecLine = -1
local stepBaseStack = 0

local function GetCurrentStackDepth()
    local depth = 0
    while debug.getinfo(depth + 2) do depth = depth + 1 end
    return depth
end

debug.sethook(function(evt)
    local lineInfo = debug.getinfo(2, 'l')
    local currLine = lineInfo.currentline or -1
    local stackDepth = GetCurrentStackDepth()

    if evt ~= 'line' or currLine <= 0 or currLine == lastExecLine then return end
    lastExecLine = currLine
    dbg_line_notify(currLine)

    local runMode = dbg_get_mode(stackDepth, stepBaseStack)
    local needPause = false
    if runMode == 0 then
        if BreakPointLine[currLine] then needPause = true end
    elseif runMode == 1 then
        needPause = true
    elseif runMode == 2 then
        if stackDepth <= stepBaseStack then needPause = true end
    end

    if needPause then
        stepBaseStack = stackDepth
        dbg_block_wait(currLine)
    end
end, 'l')
";
            _luaEnv.DoString(hookCoreScript);
        }

        public void DetachDebugHook()
        {
            if (_luaEnv == null) return;
            _luaEnv.DoString("debug.sethook()");

            CurrentState = DebugRunState.Run;
            _pendingStepMode = DebugRunState.Run;
            _debugPauseSignal.Set();
        }
        #endregion

        #region 调试控制对外接口
        public void StepInto()
        {
            _pendingStepMode = DebugRunState.StepInto;
            CurrentState = DebugRunState.StepInto;
            _debugPauseSignal.Set();
        }

        public void StepOver()
        {
            _pendingStepMode = DebugRunState.StepOver;
            CurrentState = DebugRunState.StepOver;
            if (_luaEnv != null)
                _stepOverStackBaseDepth = QueryLuaStackDepth();
            _debugPauseSignal.Set();
        }

        public void Continue()
        {
            _pendingStepMode = DebugRunState.Run;
            CurrentState = DebugRunState.Run;
            _debugPauseSignal.Set();
        }

        public void Pause()
        {
            CurrentState = DebugRunState.Paused;
        }

        public void ClearRuntimeMarkers()
        {
            SafeInvoke(() =>
            {
                lock (_renderLineQueue) _renderLineQueue.Clear();
                _executedLines.Clear();
                _lastRenderCurrentLine = -1;
                _stepOverStackBaseDepth = 0;

                CurrentState = DebugRunState.Run;
                _pendingStepMode = DebugRunState.Run;
                _debugPauseSignal.Set();

                _editor.MarkerDeleteAll(MARK_EXECUTED);
                _editor.MarkerDeleteAll(MARK_CURRENT_LINE);
            });
        }
        #endregion

        #region Lua回调实现
        private void OnLuaLineExecuted(int luaLineNum)
        {
            int editorLine = luaLineNum - LUA_LINE_OFFSET;
            if (editorLine < 0 || editorLine >= _editor.Lines.Count)
                return;

            lock (_renderLineQueue)
            {
                if (_renderLineQueue.Count == 0 || _renderLineQueue.Peek() != editorLine)
                    _renderLineQueue.Enqueue(editorLine);
            }
            SafeInvoke(RenderLineMarker);
        }

        private void BlockLuaThreadPause(int luaLineNum)
        {
            CurrentState = DebugRunState.Paused;
            _debugPauseSignal.Reset();
            _debugPauseSignal.WaitOne();
            CurrentState = _pendingStepMode;
        }

        private int GetActiveDebugMode(int stackDepth, int baseDepth)
        {
            switch (CurrentState)
            {
                case DebugRunState.Run:
                    return 0;
                case DebugRunState.StepInto:
                    return 1;
                case DebugRunState.StepOver:
                    return 2;
                default:
                    return 0;
            }
        }

        private int QueryLuaStackDepth()
        {
            if (_luaEnv == null) return 0;
            object[] ret = _luaEnv.DoString(@"
local depth = 0
while debug.getinfo(depth + 2) do depth = depth + 1 end
return depth
");
            return ret != null && ret.Length > 0 ? Convert.ToInt32(ret[0]) : 0;
        }
        #endregion

        #region UI渲染工具
        private void SafeInvoke(Action action)
        {
            if (_editor.IsDisposed) return;
            if (_editor.InvokeRequired)
                _editor.Invoke(action);
            else
                action.Invoke();
        }

        private void RenderLineMarker()
        {
            int targetLine = -1;
            lock (_renderLineQueue)
            {
                while (_renderLineQueue.Count > 0)
                {
                    targetLine = _renderLineQueue.Dequeue();
                    if (_executedLines.Add(targetLine))
                        _editor.Lines[targetLine].MarkerAdd(MARK_EXECUTED);
                }
            }
            if (targetLine < 0) return;

            if (_lastRenderCurrentLine >= 0)
                _editor.Lines[_lastRenderCurrentLine].MarkerDelete(MARK_CURRENT_LINE);
            _editor.Lines[targetLine].MarkerAdd(MARK_CURRENT_LINE);
            _lastRenderCurrentLine = targetLine;

            ScrollLineToCenter(targetLine);
        }

        private void ScrollLineToCenter(int line)
        {
            int visibleLineCount = _editor.LinesOnScreen;
            if (visibleLineCount <= 0) return;
            int firstVisible = _editor.FirstVisibleLine;
            int offset = line - firstVisible;
            int safeMargin = visibleLineCount / 4;

            if (offset >= safeMargin && offset <= visibleLineCount - safeMargin)
            {
                _editor.CurrentPosition = _editor.Lines[line].Position;
                _editor.AnchorPosition = _editor.CurrentPosition;
                return;
            }

            int targetTopLine = Math.Max(0, line - visibleLineCount / 2);
            int maxScrollTop = Math.Max(0, _editor.Lines.Count - visibleLineCount);
            targetTopLine = Math.Min(targetTopLine, maxScrollTop);

            _editor.CurrentPosition = _editor.Lines[line].Position;
            _editor.AnchorPosition = _editor.CurrentPosition;
            _editor.FirstVisibleLine = targetTopLine;
        }
        #endregion

        #region 资源释放
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposeManaged)
        {
            if (_disposed) return;
            if (disposeManaged)
            {
                _editor.DoubleClick -= Editor_DoubleClickBreakpoint;
                DetachDebugHook();
                _debugPauseSignal?.Dispose();

                lock (_renderLineQueue) _renderLineQueue.Clear();
                _executedLines.Clear();
                BreakPoints.Clear();

                _onLineExecute = null;
                _blockScriptWait = null;
                _queryDebugMode = null;
                _luaEnv = null;
            }
            _disposed = true;
        }

        ~LuaSingleDebuggerXLua() => Dispose(false);
        #endregion

        public LuaEnv LuaEnvInstance => _luaEnv;
    }
}
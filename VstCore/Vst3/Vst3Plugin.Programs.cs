using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using VstCore.Vst3;

namespace VstCore;

/// <summary>VST3 プラグイン: 内蔵プログラム (プリセット) の一覧と切り替え。</summary>
public sealed unsafe partial class Vst3Plugin
{
    // ================= プログラム (プリセット) =================
    // 優先: IUnitInfo のプログラムリスト (名前) + その unit に属する kIsProgramChange パラメータ (選択)。
    // 次点: kIsProgramChange 付きの離散パラメータだけを持つプラグイン (名前は getParamStringByValue)。
    // プログラムの切り替えは常に kIsProgramChange パラメータで行う (IUnitInfo に選択 API はない)。

    uint _programParamId;
    int _programSteps;

    /// <summary>直近の GetPrograms で何を使ったか (診断用)。</summary>
    public string ProgramSource { get; private set; } = "";

    static string Utf16At(byte* p, int maxChars)
    {
        int len = 0;
        char* c = (char*)p;
        while (len < maxChars && c[len] != '\0') len++;
        return new string(c, 0, len);
    }

    public ProgramList GetPrograms()
    {
        if (_disposed || _controller == IntPtr.Zero) return new ProgramList(Array.Empty<string>(), 0);
        return _thread.Invoke(GetProgramsOnThread);
    }

    ProgramList GetProgramsOnThread()
    {
        var none = new ProgramList(Array.Empty<string>(), 0);
        _programSteps = 0;
        ProgramSource = "none: no kIsProgramChange parameter";

        // kIsProgramChange パラメータを列挙: (id, unitId, stepCount)
        var changeParams = new List<(uint Id, int UnitId, int Steps)>();
        int paramCount = Math.Min(((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(_controller, 8))(_controller), HostProtocol.MaxItemCount);
        byte* buf = stackalloc byte[792]; // ParameterInfo: id(0) title(4) shortTitle(260) units(516) stepCount(772) default(776) unitId(784) flags(788)
        for (int i = 0; i < paramCount; i++)
        {
            new Span<byte>(buf, 792).Clear();
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int>)Fn(_controller, 9))(_controller, i, buf) != 0) continue;
            if ((*(int*)(buf + 788) & (1 << 15)) == 0) continue;
            changeParams.Add((*(uint*)buf, *(int*)(buf + 784), *(int*)(buf + 772)));
        }
        if (changeParams.Count == 0)
        {
            ProgramSource = "none: no kIsProgramChange parameter" + DescribeUnitLists();
            return none;
        }

        // IUnitInfo があれば、unit が参照するプログラムリストから名前を得る
        IntPtr unitInfo = IntPtr.Zero;
        if (QueryInterface(_controller, Tuids.IUnitInfo, out unitInfo) != 0)
            QueryInterface(_component, Tuids.IUnitInfo, out unitInfo);

        try
        {
            if (unitInfo != IntPtr.Zero)
            {
                var result = ReadUnitProgramList(unitInfo, changeParams);
                if (result != null)
                {
                    ProgramSource = "IUnitInfo program list";
                    return result;
                }
            }
            ProgramSource = unitInfo != IntPtr.Zero ? "parameter (IUnitInfo had no usable list)" : "parameter (no IUnitInfo)";
            return ReadParameterProgramList(changeParams[0]);
        }
        finally
        {
            Release(ref unitInfo);
        }
    }

    /// <summary>診断用: IUnitInfo にプログラムリストはあるが選択用パラメータがない、というケースを区別する。</summary>
    string DescribeUnitLists()
    {
        IntPtr unitInfo = IntPtr.Zero;
        try
        {
            if (QueryInterface(_controller, Tuids.IUnitInfo, out unitInfo) != 0 && QueryInterface(_component, Tuids.IUnitInfo, out unitInfo) != 0)
                return " (no IUnitInfo)";
            int lists = ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(unitInfo, 5))(unitInfo);
            byte* list = stackalloc byte[264];
            var parts = new List<string>();
            for (int i = 0; i < lists; i++)
            {
                new Span<byte>(list, 264).Clear();
                if (((delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int>)Fn(unitInfo, 6))(unitInfo, i, list) == 0)
                    parts.Add($"{Utf16At(list + 4, 128)}={*(int*)(list + 260)}");
            }
            return $" (IUnitInfo: {lists} program list(s) [{string.Join(", ", parts)}])";
        }
        finally
        {
            Release(ref unitInfo);
        }
    }

    ProgramList? ReadUnitProgramList(IntPtr unitInfo, List<(uint Id, int UnitId, int Steps)> changeParams)
    {
        // unitId -> programListId
        var unitLists = new Dictionary<int, int>();
        int unitCount = Math.Min(((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(unitInfo, 3))(unitInfo), HostProtocol.MaxItemCount);
        byte* unit = stackalloc byte[268]; // UnitInfo: id(0) parentUnitId(4) name(8) programListId(264)
        for (int i = 0; i < unitCount; i++)
        {
            new Span<byte>(unit, 268).Clear();
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int>)Fn(unitInfo, 4))(unitInfo, i, unit) != 0) continue;
            unitLists[*(int*)unit] = *(int*)(unit + 264);
        }

        // listId -> programCount
        var listCounts = new Dictionary<int, int>();
        int listCount = Math.Min(((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(unitInfo, 5))(unitInfo), HostProtocol.MaxItemCount);
        byte* list = stackalloc byte[264]; // ProgramListInfo: id(0) name(4) programCount(260)
        for (int i = 0; i < listCount; i++)
        {
            new Span<byte>(list, 264).Clear();
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int>)Fn(unitInfo, 6))(unitInfo, i, list) != 0) continue;
            listCounts[*(int*)list] = *(int*)(list + 260);
        }

        // ルート unit(0) のものを優先して、プログラムリストと結びつくパラメータを選ぶ
        foreach (var p in changeParams.OrderBy(p => p.UnitId == 0 ? 0 : 1))
        {
            if (!unitLists.TryGetValue(p.UnitId, out int listId) || listId == -1) continue;
            if (!listCounts.TryGetValue(listId, out int programs) || programs < 2) continue;
            programs = Math.Min(programs, HostProtocol.MaxItemCount);

            int steps = p.Steps > 0 ? p.Steps : programs - 1;
            var names = new string[programs];
            byte* name = stackalloc byte[256];
            for (int s = 0; s < programs; s++)
            {
                new Span<byte>(name, 256).Clear();
                bool ok = ((delegate* unmanaged[Stdcall]<IntPtr, int, int, byte*, int>)Fn(unitInfo, 7))(unitInfo, listId, s, name) == 0;
                names[s] = ok ? Utf16At(name, 128) : "";
                if (string.IsNullOrWhiteSpace(names[s])) names[s] = $"Program {s + 1}";
            }

            _programParamId = p.Id;
            _programSteps = steps;
            double value = ((delegate* unmanaged[Stdcall]<IntPtr, uint, double>)Fn(_controller, 14))(_controller, p.Id);
            return new ProgramList(names, Math.Clamp((int)Math.Round(value * steps), 0, programs - 1));
        }
        return null;
    }

    ProgramList ReadParameterProgramList((uint Id, int UnitId, int Steps) p)
    {
        if (p.Steps <= 0) return new ProgramList(Array.Empty<string>(), 0);
        p.Steps = Math.Min(p.Steps, HostProtocol.MaxItemCount - 1);

        char* text = stackalloc char[128];
        var names = new string[p.Steps + 1];
        for (int s = 0; s <= p.Steps; s++)
        {
            new Span<char>(text, 128).Clear();
            bool ok = ((delegate* unmanaged[Stdcall]<IntPtr, uint, double, char*, int>)Fn(_controller, 10))(_controller, p.Id, s / (double)p.Steps, text) == 0;
            names[s] = ok && text[0] != '\0' ? new string(text) : $"Program {s + 1}";
        }

        _programParamId = p.Id;
        _programSteps = p.Steps;
        double value = ((delegate* unmanaged[Stdcall]<IntPtr, uint, double>)Fn(_controller, 14))(_controller, p.Id);
        return new ProgramList(names, Math.Clamp((int)Math.Round(value * p.Steps), 0, p.Steps));
    }
    public void SetProgram(int index)
    {
        if (_disposed || _programSteps <= 0 || index < 0 || index > _programSteps) return;
        uint id = _programParamId;
        double value = index / (double)_programSteps;
        _thread.Invoke(() =>
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, double, int>)Fn(_controller, 15))(_controller, id, value);
            _pendingEdits.Enqueue((id, value));
        });
    }
}

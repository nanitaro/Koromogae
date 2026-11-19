using System.Collections.ObjectModel;
using System.ComponentModel;
using VstCore;
using Koromogae.Ui;

namespace Koromogae.Audio;

public sealed class PluginSlot : INotifyPropertyChanged
{
    volatile bool _enabled = true;

    public PluginSlot(IVstEffect effect) => Effect = effect;

    public IVstEffect Effect { get; }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Raise(nameof(Enabled));
        }
    }

    public string Display => PluginLabel.Format(
        Effect.Name, PluginPaths.IsVst3(Effect.Path), Effect.Bitness == PluginBitness.X86, Effect.IsFaulted);

    public void RefreshDisplay() => Raise(nameof(Display));

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>直列に処理する VST チェーン。オーディオスレッドとの競合はロックで保護する。</summary>
public sealed class PluginChain : IDisposable
{
    readonly object _lock = new();

    public ObservableCollection<PluginSlot> Slots { get; } = new();

    public void Add(PluginSlot slot)
    {
        lock (_lock) Slots.Add(slot);
    }

    public void Remove(PluginSlot slot)
    {
        lock (_lock) Slots.Remove(slot);
        slot.Effect.Dispose();
    }

    public void Move(int index, int offset)
    {
        lock (_lock)
        {
            int target = index + offset;
            if (index < 0 || index >= Slots.Count || target < 0 || target >= Slots.Count) return;
            Slots.Move(index, target);
        }
    }

    /// <summary>
    /// 有効で、停止していないプラグインが申告する遅延 (サンプル数) の合計。バイパス中のプラグインは音を素通しにするので含めない。
    /// UI スレッド専用 (Slots を UI スレッドだけが変更するので、ロックは不要)。
    /// </summary>
    public int LatencySamples
    {
        get
        {
            long total = 0;
            for (int i = 0; i < Slots.Count; i++)
            {
                var slot = Slots[i];
                if (slot.Enabled && !slot.Effect.IsFaulted) total += slot.Effect.LatencySamples;
            }
            return (int)Math.Min(total, int.MaxValue);
        }
    }

    public void Prepare(int sampleRate, int maxBlock)
    {
        lock (_lock)
        {
            foreach (var slot in Slots)
                slot.Effect.Prepare(sampleRate, maxBlock);
        }
    }

    /// <summary>オーディオスレッドから呼ぶ。UI がチェーンを編集中ならそのブロックだけスルーする。</summary>
    public void Process(float[] left, float[] right, int frames)
    {
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            for (int i = 0; i < Slots.Count; i++)
            {
                var slot = Slots[i];
                if (!slot.Enabled || slot.Effect.IsFaulted) continue;
                slot.Effect.Process(left, right, frames);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    public void Dispose()
    {
        PluginSlot[] all;
        lock (_lock)
        {
            all = Slots.ToArray();
            Slots.Clear();
        }
        foreach (var slot in all) slot.Effect.Dispose();
    }
}

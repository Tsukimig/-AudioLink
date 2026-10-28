namespace AudioLink.Core;

public sealed class ChannelMatrix
{
    private readonly float[,] _weights;
    public int InputChannels { get; }
    public int OutputChannels { get; }

    public ChannelMatrix(int inputChannels, int outputChannels, uint inputMask = 0, uint outputMask = 0)
    {
        if (inputChannels is < 1 or > 32 || outputChannels is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(inputChannels));
        InputChannels = inputChannels;
        OutputChannels = outputChannels;
        _weights = new float[outputChannels, inputChannels];
        if (inputChannels == outputChannels && (inputMask == outputMask || inputMask == 0 || outputMask == 0))
        {
            for (int i = 0; i < inputChannels; i++) _weights[i, i] = 1;
        }
        else if (inputChannels == 1)
        {
            _weights[0, 0] = 1;
            if (outputChannels > 1) _weights[1, 0] = 1;
        }
        else if (outputChannels == 1)
        {
            for (int i = 0; i < inputChannels; i++) _weights[0, i] = 1f / inputChannels;
        }
        else if (outputChannels == 2 && inputChannels > 2)
        {
            // Windows speaker bit order: FL, FR, FC, LFE, BL, BR, FLC, FRC, BC, SL, SR.
            uint[] speakers = ExpandMask(inputMask, inputChannels);
            for (int i = 0; i < inputChannels; i++)
            {
                switch (speakers[i])
                {
                    case 1: _weights[0, i] = 1; break;
                    case 2: _weights[1, i] = 1; break;
                    case 16: case 64: case 512: _weights[0, i] = 0.7071f; break;
                    case 32: case 128: case 1024: _weights[1, i] = 0.7071f; break;
                    case 8: _weights[0, i] = _weights[1, i] = 0.25f; break;
                    default: _weights[0, i] = _weights[1, i] = 0.7071f; break;
                }
            }
            // Leave headroom for coherent full-scale surround content.
            for (int output = 0; output < 2; output++)
            {
                float sum = 0;
                for (int i = 0; i < inputChannels; i++) sum += _weights[output, i];
                for (int i = 0; i < inputChannels; i++) _weights[output, i] /= Math.Max(1, sum);
            }
        }
        else
        {
            uint[] inputs = ExpandMask(inputMask, inputChannels);
            uint[] outputs = ExpandMask(outputMask, outputChannels);
            for (int output = 0; output < outputChannels; output++)
            {
                int match = Array.IndexOf(inputs, outputs[output]);
                if (match >= 0) _weights[output, match] = 1;
            }
        }
    }

    public void Mix(ReadOnlySpan<float> inputFrame, Span<float> outputFrame)
    {
        for (int output = 0; output < OutputChannels; output++)
        {
            float sample = 0;
            for (int input = 0; input < InputChannels; input++) sample += inputFrame[input] * _weights[output, input];
            outputFrame[output] = sample;
        }
    }

    private static uint[] ExpandMask(uint mask, int channels)
    {
        if (mask == 0) mask = channels switch { 1 => 4, 2 => 3, 6 => 0x3F, 8 => 0x63F, _ => uint.MaxValue };
        var result = new uint[channels];
        int index = 0;
        for (int bit = 0; bit < 32 && index < channels; bit++)
            if ((mask & (1u << bit)) != 0) result[index++] = 1u << bit;
        return result;
    }
}

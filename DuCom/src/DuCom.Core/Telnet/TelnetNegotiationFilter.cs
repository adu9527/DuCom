namespace DuCom.Core.Telnet;

/// <summary>Removes Telnet IAC negotiation sequences while preserving application bytes.</summary>
public sealed class TelnetNegotiationFilter
{
    private const byte Iac = 255;
    private const byte Se = 240;
    private const byte Sb = 250;
    private const byte Will = 251;
    private const byte Wont = 252;
    private const byte Do = 253;
    private const byte Dont = 254;

    private FilterState _state;

    public byte[] Filter(ReadOnlySpan<byte> input)
    {
        List<byte>? output = null;
        foreach (byte value in input)
        {
            switch (_state)
            {
                case FilterState.Data when value == Iac:
                    _state = FilterState.Iac;
                    break;
                case FilterState.Data:
                    (output ??= []).Add(value);
                    break;
                case FilterState.Iac when value == Iac:
                    (output ??= []).Add(Iac);
                    _state = FilterState.Data;
                    break;
                case FilterState.Iac when value is Will or Wont or Do or Dont:
                    _state = FilterState.Option;
                    break;
                case FilterState.Iac when value == Sb:
                    _state = FilterState.Subnegotiation;
                    break;
                case FilterState.Iac:
                    _state = FilterState.Data;
                    break;
                case FilterState.Option:
                    _state = FilterState.Data;
                    break;
                case FilterState.Subnegotiation when value == Iac:
                    _state = FilterState.SubnegotiationIac;
                    break;
                case FilterState.Subnegotiation:
                    break;
                case FilterState.SubnegotiationIac when value == Se:
                    _state = FilterState.Data;
                    break;
                case FilterState.SubnegotiationIac when value != Iac:
                    _state = FilterState.Subnegotiation;
                    break;
            }
        }

        return output?.ToArray() ?? [];
    }

    private enum FilterState
    {
        Data,
        Iac,
        Option,
        Subnegotiation,
        SubnegotiationIac,
    }
}

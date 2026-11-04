using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace DuCom.PluginHost.Core;

// Edge TTS v2 token algorithm adapted from edge-tts (MIT, rany2 and contributors).
// Kept separate from transport so protocol vectors can be checked without network access.
public static class EdgeSpeechToken
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    private static readonly uint[] Initial = [0x6A09E667,0xBB67AE85,0x3C6EF372,0xA54FF53A,0x510E527F,0x9B05688C,0x1F83D9AB,0x5BE0CD19];
    private static readonly uint[] Seed = [0xFD9FBEA2,0x91243CE7,0xBE499094,0xCD2E1E0A,0x6C006F49,0x04BD35AB,0xFEB0A7E4,0x03EFFBD2];
    private static readonly uint[] Schedule = [0xEB24BF9E,0x1558691F,0xA0C09EE2,0x0CFA32BA,0xA622EE8C,0xD1DD9491,0xC0DD25A0,0xF9E70AB6,0xF0C31573,0x7058CE4E,0x7188E606,0x00E5AB33,0xD258B060,0x72766564];
    private static readonly uint[] K = [
        0x428A2F98,0x71374491,0xB5C0FBCF,0xE9B5DBA5,0x3956C25B,0x59F111F1,0x923F82A4,0xAB1C5ED5,
        0xD807AA98,0x12835B01,0x243185BE,0x550C7DC3,0x72BE5D74,0x80DEB1FE,0x9BDC06A7,0xC19BF174,
        0xE49B69C1,0xEFBE4786,0x0FC19DC6,0x240CA1CC,0x2DE92C6F,0x4A7484AA,0x5CB0A9DC,0x76F988DA,
        0x983E5152,0xA831C66D,0xB00327C8,0xBF597FC7,0xC6E00BF3,0xD5A79147,0x06CA6351,0x14292967,
        0x27B70A85,0x2E1B2138,0x4D2C6DFC,0x53380D13,0x650A7354,0x766A0ABB,0x81C2C92E,0x92722C85,
        0xA2BFE8A1,0xA81A664B,0xC24B8B70,0xC76C51A3,0xD192E819,0xD6990624,0xF40E3585,0x106AA070,
        0x19A4C116,0x1E376C08,0x2748774C,0x34B0BCB5,0x391C0CB3,0x4ED8AA4A,0x5B9CCA4F,0x682E6FF3,
        0x748F82EE,0x78A5636F,0x84C87814,0x8CC70208,0x90BEFFFA,0xA4506CEB,0xBEF9A3F7,0xC67178F2];
    private static readonly (uint A,uint B)[] Coefficients = [
        (0xE82E91B1,0xB32700D0),(0x44DE24C6,0x88DFF51D),(0x3BEB65BC,0x29514038),(0xC89C5933,0xB328CBDF),
        (0x8D1EC211,0xD405B4D5),(0x314EF5D5,0x26E55F3C),(0xE92491B1,0x607801C6),(0xF0A5B9D5,0x82B2C1FB),
        (0x06064CF3,0xFA51A3DA),(0xDA79FA60,0x207CC464),(0xBED20560,0x3C3C74F3),(0x89F9C9F8,0xD2D14B61),
        (0x35FA38C6,0xBB04A2C1),(0xCB49E3C6,0xC5626167)];
    private static uint S0(uint x) => BitOperations.RotateRight(x,7)^BitOperations.RotateRight(x,18)^(x>>3);
    private static uint S1(uint x) => BitOperations.RotateRight(x,17)^BitOperations.RotateRight(x,19)^(x>>10);
    private static uint Swap(uint x) => BitOperations.RotateLeft(x,16);
    private static void Expand(uint[] w,int start)
    { unchecked { for(int i=start;i<64;i++) w[i]=S1(w[i-2])+w[i-7]+S0(w[i-15])+w[i-16]; } }
    private static uint[] Rounds(uint[] state,uint[] words,int first)
    {
        uint a=state[0],b=state[1],c=state[2],d=state[3],e=state[4],f=state[5],g=state[6],h=state[7];
        unchecked
        {
            for(int i=first;i<64;i++)
            {
                uint t1=h+(BitOperations.RotateRight(e,6)^BitOperations.RotateRight(e,11)^BitOperations.RotateRight(e,25))+(g^(e&(f^g)))+(first==14&&i==14?0xC9DC06A7:K[i])+words[i];
                uint t2=(BitOperations.RotateRight(a,2)^BitOperations.RotateRight(a,13)^BitOperations.RotateRight(a,22))+((a&b)|(c&(a|b)));
                h=g;g=f;f=e;e=d+t1;d=c;c=b;b=a;a=t1+t2;
            }
        }
        return [a,b,c,d,e,f,g,h];
    }
    public static string Generate(DateTimeOffset time)
    {
        long seconds=time.ToUnixTimeSeconds()+11_644_473_600L;
        seconds-=seconds%300;
        byte[] data=Encoding.ASCII.GetBytes((seconds*10_000_000L).ToString(CultureInfo.InvariantCulture)+TrustedClientToken);
        uint[] w=new uint[64];
        w[14]=(uint)(data[0]<<16|data[1]<<8|data[2]);
        w[15]=BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(3,4));
        unchecked
        {
            for(int i=16;i<30;i++)
            {
                uint v=Schedule[i-16];
                if(i-2>=14) v+=i==16?S1(w[i-2])^0x000B92C0:S1(w[i-2]);
                if(i-7>=14) v+=w[i-7];
                if(i-15>=14) v+=i==29?S0(w[i-15])^0x059C0B80:S0(w[i-15]);
                w[i]=v;
            }
            w[30]=S1(w[28])+w[23]+S0(w[15])+w[14]+0xC9DC06A7-K[14];
            Expand(w,31);
            uint[] state=Rounds(Seed,w,14);
            for(int i=0;i<8;i++) state[i]+=Initial[i];
            int length=57+data.Length;
            int zeros=(55-length)%64;if(zeros<0) zeros+=64;
            byte[] padded=new byte[data.Length-7+1+zeros+8];
            data.AsSpan(7).CopyTo(padded);padded[data.Length-7]=0x80;
            BinaryPrimitives.WriteUInt64BigEndian(padded.AsSpan(padded.Length-8),(ulong)length*8);
            for(int offset=0;offset<padded.Length;offset+=64)
            {
                for(int i=0;i<16;i++) w[i]=BinaryPrimitives.ReadUInt32BigEndian(padded.AsSpan(offset+i*4,4));
                Expand(w,16);uint[] work=Rounds(state,w,0);
                for(int i=0;i<8;i++) state[i]+=work[i];
            }
            byte[] digest=new byte[32];
            for(int i=0;i<8;i++) BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i*4,4),state[i]);
            uint ka=BinaryPrimitives.ReadUInt32LittleEndian(digest)|1,kb=BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4))|1;
            uint[] factors=[ka*0x82893933,kb*0x82893933];
            List<uint> terms=[], offsets=[];
            for(int i=0;i<Coefficients.Length;i++) {terms.Add(Coefficients[i].A*factors[i%2]);offsets.Add(Coefficients[i].B);}
            byte[] words=new byte[((data.Length+1+7)/8)*8];data.CopyTo(words,1);
            for(int i=0;i<words.Length/4;i++)
            {
                uint word=BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(i*4,4));
                uint term=word*(i%2==0?ka:kb),off=Swap(word)*(i%2==0?0xEF0569FB:0xC31713DB);
                if(i==0){term+=0xF2F90B1A*factors[0];off+=0x0B1A0000;}
                terms.Add(term);offsets.Add(off);
            }
            uint swapAcc=0,swapLast=0,s=0,acc=0;
            for(int i=0;i<terms.Count;i+=2)
            {
                uint x=terms[i],y=terms[i+1],q=Swap(offsets[i]+x)*0x76F50F34;
                swapAcc+=Swap(q)*0xC3EFEA97+q;
                q=Swap(offsets[i+1]+y)*0x1881CE3C;swapLast=Swap(q)*0x35BD1EC9+q;swapAcc+=swapLast;
                s=s*ka+x;s=Swap(s)*0xCF98B111;s=Swap(s)*0x87085B9F;s=Swap(s)*0x12CEB96D;s=Swap(s)*0x257E1D83;acc+=s;
                uint t=s*kb+y;t=Swap(t)*0xA27416F5;t=Swap(t)*0xD38396FF;s=Swap(Swap(t)*0x7C932B89)*0xBFA49F69;acc+=s;
            }
            byte[] cs=new byte[8];BinaryPrimitives.WriteUInt32LittleEndian(cs,swapLast^s);BinaryPrimitives.WriteUInt32LittleEndian(cs.AsSpan(4),swapAcc^acc);
            return Convert.ToHexString(cs)+"-"+Convert.ToHexString(digest);
        }
    }
}

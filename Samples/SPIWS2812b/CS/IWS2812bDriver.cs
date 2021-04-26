using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IoT.Windows10.ws2812b
{
    public interface IWS2812bDriver
    {
        bool Write(int pixel, byte red, byte green, byte blue);
        bool Write(int pixel, Color color);
        bool Clean();
        void RefreshLeds();
    }
}

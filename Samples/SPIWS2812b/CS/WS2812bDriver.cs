using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Spi;

namespace IoT.Windows10.ws2812b
{
    public class WS2812bDriver : IWS2812bDriver
    {
        private SpiDevice spiDevice;
        private byte[] bufferLedSPI;
        private const byte ledBitOn = 0b1110;
        private const byte ledBitOff = 0b1100;

        public async Task InitializeAsync(int pixels, string spiFriendlyName = "SPI0", int chipSelectLine = 0)
        {
            //we use 4bits for each byte of ws2812b, so we need 12 bytes to set the 24 bytes of led
            var lenghtBuffer = pixels * 12;
            bufferLedSPI = new byte[lenghtBuffer];

            var settings = new SpiConnectionSettings(chipSelectLine);
            settings.ClockFrequency = 4000000;

            var spi = SpiDevice.GetDeviceSelector(spiFriendlyName);
            var deviceInformation = await DeviceInformation.FindAllAsync(spi);
            spiDevice = await SpiDevice.FromIdAsync(deviceInformation[0].Id, settings);
        }

        public bool Write(int pixel, byte red, byte green, byte blue)
        {
            byte[] Colors = new byte[] { green, red, blue };
            
            if (spiDevice != null)
            {
                int indexPixel = pixel * 12;

                foreach (byte Color in Colors)
                { 
                    byte[] EncodedBytes = Encode(Color);

                    foreach (byte Data in EncodedBytes)
                    {
                        bufferLedSPI[indexPixel++] = Data;
                    }
                }
                return true;
            }

            return false;
        }

        private byte[] Encode(byte ColorByte)
        {

            byte[] byteArray = new byte[4];
            int indexBit = 0;
            int indexByte = 3;
            byte mask = 0x80;

            while(indexBit < 8)
            {
                mask = (byte)(0x80 >> indexBit);
                indexBit++;

                if ((ColorByte & mask) > 0)
                    byteArray[indexByte] = ledBitOn << 4;
                else
                    byteArray[indexByte] = ledBitOff << 4;

                
                mask = (byte)(1 >> indexBit);
                indexBit++;

                if ((ColorByte & mask) > 0)
                    byteArray[indexByte] |= ledBitOn;
                else
                    byteArray[indexByte] |= ledBitOff;

                indexByte--;
            }

            return byteArray;
        }

        public bool Write(int pixel, Color color)
        {
            throw new NotImplementedException();
        }

        public void RefreshLeds()
        {
            spiDevice.Write(bufferLedSPI);
            Thread.Sleep(1);
        }

        public bool Clean()
        {
            byte cleanByteContent = ledBitOff << 4 | ledBitOff;
            try
            {
                for (int indexLedByte = 0; indexLedByte < bufferLedSPI.Length; indexLedByte++)
                    bufferLedSPI[indexLedByte] = cleanByteContent;
            }
            catch(Exception ex)
            {
                return false;
            }

            return true;
        }
    }
}

using System;
using System.Net;
using System.Net.Sockets;

namespace NimbusX.Core.Ports;

/// <summary>Finds a free loopback TCP port for an rclone rc endpoint.</summary>
public static class PortAllocator
{
    /// <summary>Asks the OS for an unused port bound to 127.0.0.1.</summary>
    public static int GetFreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // ignore
            }
        }
    }

    /// <summary>True when something is listening on <paramref name="port"/> on loopback.</summary>
    public static bool IsListening(int port, TimeSpan? timeout = null)
    {
        if (port <= 0 || port > 65535)
        {
            return false;
        }

        try
        {
            using TcpClient client = new();
            IAsyncResult result = client.BeginConnect(IPAddress.Loopback, port, null, null);
            bool connected = result.AsyncWaitHandle.WaitOne(timeout ?? TimeSpan.FromMilliseconds(400));
            if (!connected)
            {
                return false;
            }

            client.EndConnect(result);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

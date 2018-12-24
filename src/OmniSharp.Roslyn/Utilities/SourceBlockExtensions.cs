using System.Collections.Generic;
using System.Threading.Tasks.Dataflow;

namespace OmniSharp
{
    public static class SourceBlockExtensions
    {
        // http://stackoverflow.com/questions/25339029/bufferblock-deadlock-with-outputavailableasync-after-tryreceiveall
        public static IList<T> ReceiveAll<T>(this IReceivableSourceBlock<T> buffer)
        {
            /* Microsoft TPL Dataflow version 4.5.24 contains a bug in TryReceiveAll
             * Hence this function uses TryReceive until nothing is available anymore
             * */
            IList<T> receivedItems = new List<T>();
            T receivedItem = default(T);
            while (buffer.TryReceive(out receivedItem))
            {
                receivedItems.Add(receivedItem);
            }
            return receivedItems;
        }
    }
}

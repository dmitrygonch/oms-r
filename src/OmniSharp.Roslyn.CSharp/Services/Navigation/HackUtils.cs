using OmniSharp.Models;
using OmniSharp.Options;
using System;
using System.IO;
using System.Linq;

namespace OmniSharp.Roslyn.CSharp.Services.Navigation
{
    internal static class HackUtils
    {
        public static bool TryGetSymbolTextForRequest(Request request, out string symbolText)
        {
            symbolText = null;
            if (!HackOptions.Enabled)
            {
                return false;
            }

            string line = File.ReadAllLines(request.FileName).Skip(request.Line).Take(1).FirstOrDefault();

            if (line == null)
            {
                // The file potentially was changed in the editor but hasn't been saved yet
                return false;
            }

            if (line.Trim().Length >= 2 && line.Trim().Substring(0, 2) == @"//")
            {
                // Either the file was potentially changed in the editor but hasn't been saved yet or user just pressed F12 inside of a comment
                return false;
            }

            //Locate where the string starts
            int startPosition = request.Column;
            do
            {
                startPosition--;
            } while (startPosition >= 0 && Char.IsLetter(line[startPosition]));
            startPosition++;

            //Locate where the string ends
            int endPosition = request.Column;

            while (endPosition < line.Length - 1 && Char.IsLetterOrDigit(line[endPosition]))
            {
                endPosition++;
            }

            symbolText = line.Substring(startPosition, endPosition - startPosition);

            return symbolText.Length > 0;
        }
    }
}

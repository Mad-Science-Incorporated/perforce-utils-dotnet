using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

/// <summary>
/// Single file, zero-dependency Perforce helper library.
/// </summary>
namespace Madscience.Perforce
{

    /// <summary>
    /// 
    /// </summary>
    public enum AnnotateChange 
    { 
        add,
        edit,
        delete
    }


    /// <summary>
    /// 
    /// </summary>
    public class Annotate
    { 
        /// <summary>
        /// Revision annotation was taken at
        /// </summary>
        public int Revision { get; set; }

        public string File { get; set; }

        public AnnotateChange Change {get;set; }

        public IList<AnnotateLine> Lines {get;set; }

        public Annotate()
        { 
            Lines = new List<AnnotateLine>();
            File= string.Empty;
        }
    }


    /// <summary>
    /// 
    /// </summary>
    public class AnnotateLine
    {
        /// <summary>
        /// Revision that changed this line
        /// </summary>
        public int Revision {get;set; }

        public string Text { get; set; }
        
        /// <summary>
        /// Line number
        /// </summary>
        public int LineNumber { get; set; }

        public AnnotateLine()
        { 
            Text = string.Empty;
        }
    }


    /// <summary>
    /// 
    /// </summary>
    public class Change
    {
        public int Revision { get; set; }
        public string Workspace { get; set; }
        public DateTime Date { get; set; }
        public string User { get; set; }
        public string Description { get; set; }
        public IEnumerable<ChangeFile> Files { get; set; }

        public Change()
        {
            Workspace = string.Empty;
            User = string.Empty;
            Description = string.Empty;
            Files = new List<ChangeFile>();
        }
    }


    /// <summary>
    /// 
    /// </summary>
    public class ChangeFile
    {
        public string File { get; set; }
        public string Change { get; set; }
        public IEnumerable<string> Differences { get; set; }

        public ChangeFile()
        {
            File = string.Empty;
            Change = string.Empty;
            Differences = new string[] { };
        }
    }


    /// <summary>
    /// 
    /// </summary>
    internal class ShellResult
    {
        public int ExitCode { get; set; }
        public IEnumerable<string> StdOut { get; set; }
        public IEnumerable<string> StdErr { get; set; }

        public ShellResult()
        {
            this.StdOut = new string[] { };
            this.StdErr = new string[] { };
        }
    }


    /// <summary>
    /// 
    /// </summary>
    public class PerforceUtils
    {
        private enum ShellType
        { 
            Sh, Cmd
        }


        /// <summary>
        /// Runs a shell command synchronously, returns concatenated stdout, stderr and error code.
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        private static ShellResult Run(string command, ShellType shellType)
        {
            Process cmd = new Process();
            
            if (shellType == ShellType.Cmd)
            {
                cmd.StartInfo.FileName = "cmd.exe";
                cmd.StartInfo.Arguments = $"/k {command}";
            } 
            else 
            {
                cmd.StartInfo.FileName = "sh";
                cmd.StartInfo.Arguments = $"-c {command}";
            }

            cmd.StartInfo.RedirectStandardInput = true;
            cmd.StartInfo.RedirectStandardOutput = true;
            cmd.StartInfo.RedirectStandardError = true;
            cmd.StartInfo.CreateNoWindow = true;
            cmd.StartInfo.UseShellExecute = false;
            cmd.Start();

            cmd.StandardInput.Flush();
            cmd.StandardInput.Close();
            //cmd.WaitForExit(); // hangs on windows

            List<string> stdOut = new List<string>();
            List<string> stdErr = new List<string>();

            while (!cmd.StandardOutput.EndOfStream)
            {
                string line = cmd.StandardOutput.ReadLine();
                stdOut.Add(line);
            }

            while (!cmd.StandardError.EndOfStream)
            {
                string line = cmd.StandardError.ReadLine();
                stdErr.Add(line);
            }

            return new ShellResult
            {
                StdOut = stdOut,
                StdErr = stdErr,
                ExitCode = cmd.ExitCode
            };
        }


        /// <summary>
        /// Converts windows line endings to unix
        /// </summary>
        /// <param name="input"></param>
        /// <returns></returns>
        private static string StandardizeLineEndings(string input)
        {
            return input.Replace("/\r\n", "\n");
        }


        /// <summary>
        /// Compact and safe regex find - tries to find a string in another string, if found, returns result, else returns empty string.
        /// </summary>
        /// <param name="text"></param>
        /// <param name="regexPattern"></param>
        /// <returns></returns>
        private static string Find(string text, string regexPattern, RegexOptions options = RegexOptions.None)
        {
            Match match = new Regex(regexPattern, options).Match(text);
            if (!match.Success || match.Groups.Count < 2)
                return string.Empty;

            return match.Groups[1].Value;
        }


        /// <summary>
        /// Creates a perforce login session against the given host
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="host"></param>
        private static void EnsureSession(string username, string password, string host)
        {
            ShellResult result = Run($"p4 set P4USER={username}", ShellType.Cmd);
            if (result.ExitCode != 0)
                throw new Exception($"Failed to set user, got code {result.ExitCode} - {result.StdErr}");

            result = Run($"p4 set P4PORT={host}", ShellType.Cmd);
            if (result.ExitCode != 0)
                throw new Exception($"Failed to set port, got code {result.ExitCode} - {result.StdErr}");

            Run($"echo {password}|p4 login", ShellType.Cmd);
            if (result.ExitCode != 0)
                throw new Exception($"Failed to login, got code {result.ExitCode} - {result.StdErr}");
        }


        /// <summary>
        /// Gets detailed contents of a change using the P4 describe command.
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="host"></param>
        /// <param name="revision"></param>
        /// <returns></returns>
        public static Change Describe(string username, string password, string host, int revision)
        {
            EnsureSession(username, password, host);
            string command = $"p4 describe {revision}";

            ShellResult result = Run(command, ShellType.Cmd);
            if (result.ExitCode != 0)
                throw new Exception($"P4 command {command} exited with code {result.ExitCode} : {result.StdErr}");

            string describeRaw = string.Join("\\n", result.StdOut);
            
            return ParseDescribe(describeRaw);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="rawDescribe"></param>
        /// <param name="parseDifferences"></param>
        /// <returns></returns>
        private static Change ParseDescribe(string rawDescribe, bool parseDifferences = true)
        {
            // convert all windows linebreaks to unix 
            rawDescribe = StandardizeLineEndings(rawDescribe);

            // s modifier selects across multiple lines
            string descriptionRaw = Find(rawDescribe, @"\\n\\n(.*?)\\n\\nAffected files ...", RegexOptions.IgnoreCase & RegexOptions.Multiline).Trim();
            IList<ChangeFile> files = new List<ChangeFile>();

            // affected files is large block listing all files which have been affected by revision
            string affectedFilesRaw = Find(rawDescribe, @"\\n\\nAffected files ...\\n\\n(.*?)\\n\\nDifferences ...", RegexOptions.IgnoreCase);
            // multiline grab
            string differencesRaw = Find(rawDescribe, @"\\n\\nDifferences ...\\n\\n(.*)", RegexOptions.IgnoreCase);

            affectedFilesRaw = affectedFilesRaw == null ? string.Empty : affectedFilesRaw;
            IEnumerable<string> affectedFiles = affectedFilesRaw.Split("\\n");

            IEnumerable<string> differences = differencesRaw.Split(@"\\n==== ");

            foreach (string affectedFile in affectedFiles)
            {
                Match match = new Regex(@"... (.*)#[\d]+ (delete|add|edit)$", RegexOptions.IgnoreCase).Match(affectedFile);
                if (!match.Success || match.Groups.Count < 2)
                    continue;

                ChangeFile item = new ChangeFile
                {
                    File = match.Groups[1].Value,
                    Change = match.Groups[2].Value
                };

                // try to get difference
                if (parseDifferences)
                    foreach (string difference in differences)
                    {
                        string file = Find(difference, @" (.*?)#[\d]+ .+ ====");
                        if (file == item.File)
                            item.Differences = Find(difference, @"#.+====(.*)")
                                .Split("\\n\\n", StringSplitOptions.RemoveEmptyEntries);
                    }

                files.Add(item);
            }

            IEnumerable<string> description = descriptionRaw.Split("\\n", StringSplitOptions.RemoveEmptyEntries);
            description = description.Select(line => line.Trim());

            return new Change
            {
                Revision = int.Parse(Find(rawDescribe, @"change ([\d]+) ", RegexOptions.IgnoreCase)),
                Workspace = Find(rawDescribe, @"change [\d]+ by.+@(.*) on ", RegexOptions.IgnoreCase),
                Date = DateTime.Parse(Find(rawDescribe, @"change [\d]+ by.+ on (.*?) ", RegexOptions.IgnoreCase)),
                User = Find(rawDescribe, @"change [\d]+ by (.*)@", RegexOptions.IgnoreCase),
                Files = files,
                Description = string.Join(" ", description)
            };
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="host"></param>
        /// <param name="filePath"></param>
        /// <param name="revision"></param>
        /// <returns></returns>
        public static Annotate Annotate(string username, string password, string host, string filePath, int? revision = null)
        {
            EnsureSession(username, password, host);

            string revisionSwitch = string.Empty;
            if (revision.HasValue)
                revisionSwitch = $"@{revision.Value}";

            string command = $"p4 annotate -c {filePath}{revisionSwitch}";
            ShellResult result = Run(command, ShellType.Cmd);
            if (result.ExitCode != 0)
                throw new Exception($"P4 command {command} exited with code {result.ExitCode} : {result.StdErr}");

            return ParseAnnotate(result.StdOut);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="lines"></param>
        /// <returns></returns>
        private static Annotate ParseAnnotate(IEnumerable<string> lines)
        {
            lines = lines.Where(line => !string.IsNullOrEmpty(line));
            int revision = 0;
            string file = string.Empty;
            AnnotateChange change = AnnotateChange.add;
            IList<AnnotateLine> annotateLines = new List<AnnotateLine>();

            // parse out first line, this contains descriptoin
            if (lines.Count() > 0)
            {
                file = Find(lines.ElementAt(0), @"^(.*?) -");
                revision = int.Parse(Find(lines.ElementAt(0), @" change (.*?) "));
                change = (AnnotateChange)Enum.Parse(typeof(AnnotateChange), Find(lines.ElementAt(0), @" - (.*?) change "));
            }

            if (lines.Count() > 1)
                // start start 2nd item in array
                for (int i = 1; i < lines.Count(); i++)
                {
                    // normally first text on annotate line is "revisionnumber: ", but there can be other console noise at end, we 
                    // search for confirmed rev nr, and if not found, we ignore line
                    string rawRevision = Find(lines.ElementAt(i), @"^(.*?): ");
                    if (string.IsNullOrEmpty(rawRevision))
                        continue;

                    AnnotateLine line = new AnnotateLine();
                    annotateLines.Add(line);
                    line.Revision = int.Parse(rawRevision);
                    line.Text = Find(lines.ElementAt(i), @":(.*)$");
                    line.LineNumber = i;
                }

            return new Annotate
            {
                File = file,
                Change = change,
                Revision = revision,
                Lines = annotateLines
            };
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="username"></param>
        /// <param name="password"></param>
        /// <param name="host"></param>
        /// <param name="max"></param>
        /// <param name="path"></param>
        /// <returns></returns>
        public static IEnumerable<Change> Changes(string username, string password, string host, int max = 0, string path = "//...")
        {
            EnsureSession(username, password, host);

            string maxModifier = max > 0 ? $"-m {max}" : string.Empty;
            string command = $"p4 changes {maxModifier} -l {path}";
            //string command = $"p4 changes";

            ShellResult result = Run(command, ShellType.Cmd);
            if (result.ExitCode != 0)
                throw new Exception($"P4 command {command} exited with code {result.ExitCode} : {string.Join("\\n", result.StdErr)}");

            return ParseChanges(result.StdOut);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="rawChanges"></param>
        /// <returns></returns>
        private static IEnumerable<Change> ParseChanges(IEnumerable<string> rawChanges)
        {
            List<Change> changes = new List<Change>();
            Change currentChange = new Change();

            foreach (string changeLine in rawChanges)
            {
                if (changeLine.StartsWith("Change "))
                {
                    currentChange = new Change();
                    changes.Add(currentChange);

                    currentChange.Revision = int.Parse(Find(changeLine, @"change ([\d]+) ", RegexOptions.IgnoreCase));
                    currentChange.User = Find(changeLine, @"change [\d]+ on .+ by (.*)@", RegexOptions.IgnoreCase);
                    currentChange.Workspace = Find(changeLine, @"change [\d]+ on .+ by .+@(.*)", RegexOptions.IgnoreCase);
                    currentChange.Date = DateTime.Parse(Find(changeLine, @"change [\d]+ on (.*?) by ", RegexOptions.IgnoreCase));
                }
                else
                {
                    // remove tab chars, replace them with spaces as they server as spaces in formatted p4 messages.
                    // trim to remove those spaces when added add beginning of commit message, where the \t is effectively used as a newline
                    currentChange.Description += changeLine.Replace(@"\t", " ").Trim();
                }
            }

            return changes;
        }
    }
}

﻿namespace Swashbuckle.AspNetCore.Cli;

internal class CommandRunner(string commandName, string commandDescription, TextWriter output)
{
    private readonly Dictionary<string, string> _argumentDescriptors = [];
    private readonly Dictionary<string, OptionDescriptor> _optionDescriptors = [];
    private Func<IDictionary<string, string>, int> _runFunc = (_) => 1;
    private readonly List<CommandRunner> _subRunners = [];
    private readonly TextWriter _output = output;

    public string CommandName { get; private set; } = commandName;

    public string CommandDescription { get; private set; } = commandDescription;

    public void Argument(string name, string description)
    {
        _argumentDescriptors.Add(name, description);
    }

    public void Option(string name, string description, bool isFlag = false)
    {
        if (!name.StartsWith("--"))
        {
            throw new ArgumentException("name of option must begin with --");
        }

        _optionDescriptors.Add(name, new OptionDescriptor { Description = description, IsFlag = isFlag });
    }

    public void OnRun(Func<IDictionary<string, string>, int> runFunc)
    {
        _runFunc = runFunc;
    }

    public void SubCommand(string name, string description, Action<CommandRunner> configAction)
    {
        var runner = new CommandRunner($"{CommandName} {name}", description, _output);
        configAction(runner);
        _subRunners.Add(runner);
    }

    public int Run(IEnumerable<string> args)
    {
        if (args.Any())
        {
            var subRunner = _subRunners.FirstOrDefault(r => r.CommandName.Split(' ').Last() == args.First());
            if (subRunner != null) return subRunner.Run(args.Skip(1));
        }

        if (_subRunners.Count != 0 || !TryParseArgs(args, out IDictionary<string, string> namedArgs))
        {
            PrintUsage();
            return 1;
        }

        return _runFunc(namedArgs);
    }

    private bool TryParseArgs(IEnumerable<string> args, out IDictionary<string, string> namedArgs)
    {
        namedArgs = new Dictionary<string, string>();
        var argsQueue = new Queue<string>(args);

        // Process options first
        while (argsQueue.Count != 0 && argsQueue.Peek().StartsWith("--"))
        {
            // Ensure it's a known option
            var name = argsQueue.Dequeue();
            if (!_optionDescriptors.TryGetValue(name, out OptionDescriptor optionDescriptor))
            {
                return false;
            }

            // If it's not a flag, ensure it's followed by a corresponding value
            if (!optionDescriptor.IsFlag && (argsQueue.Count == 0 || argsQueue.Peek().StartsWith("--")))
            {
                return false;
            }

            namedArgs.Add(name, (!optionDescriptor.IsFlag ? argsQueue.Dequeue() : null));
        }

        // Process required args - ensure corresponding values are provided
        foreach (var name in _argumentDescriptors.Keys)
        {
            if (argsQueue.Count == 0 || argsQueue.Peek().StartsWith("--"))
            {
                return false;
            }
            namedArgs.Add(name, argsQueue.Dequeue());
        }

        return argsQueue.Count == 0;
    }

    private void PrintUsage()
    {
        if (_subRunners.Count != 0)
        {
            // List sub commands
            _output.WriteLine(CommandDescription);
            _output.WriteLine("Commands:");
            foreach (var runner in _subRunners)
            {
                var shortName = runner.CommandName.Split(' ').Last();
                if (shortName.StartsWith('_'))
                {
                    continue; // convention to hide commands
                }

                _output.WriteLine($"  {shortName}:  {runner.CommandDescription}");
            }
            _output.WriteLine();
        }
        else
        {
            // Usage for this command
            var optionsPart = _optionDescriptors.Count != 0 ? "[options] " : "";
            var argParts = _argumentDescriptors.Keys.Select(name => $"[{name}]");
            _output.WriteLine($"Usage: {CommandName} {optionsPart}{string.Join(" ", argParts)}");
            _output.WriteLine();

            // Arguments
            foreach (var entry in _argumentDescriptors)
            {
                _output.WriteLine($"{entry.Key}:");
                _output.WriteLine($"  {entry.Value}");
                _output.WriteLine();
            }

            // Options
            if (_optionDescriptors.Count != 0)
            {
                _output.WriteLine("options:");
                foreach (var entry in _optionDescriptors)
                {
                    _output.WriteLine($"  {entry.Key}:  {entry.Value.Description}");
                }
                _output.WriteLine();
            }
        }
    }

    private struct OptionDescriptor
    {
        public string Description;
        public bool IsFlag;
    }
}

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CSharp;
using NClap.Metadata;
using NClap.Parser;
using NClap.Types;

namespace MdCompile
{
    class Program : IDisposable
    {
        class Arguments
        {
            [PositionalArgument(ArgumentFlags.Required)]
            [MustExist(PathExists.AsFile)]
            public FileSystemPath Path { get; set; }

	        [NamedArgument(ArgumentFlags.Multiple, LongName = "Reference", ShortName = "Ref")]
	        public List<FileSystemPath> ReferenceAssemblyPaths { get; set; } = new List<FileSystemPath>();

            [NamedArgument]
            public bool Verbose { get; set; }
        }

        private readonly Arguments _args;

        private readonly CSharpCodeProvider _codeProvider = new CSharpCodeProvider(
            new Dictionary<string, string>
            {
                { "CompilerVersion", "v4.0" }
            });

        private Program(Arguments args)
        {
            _args = args;
        }

        static int Main(string[] args)
        {
            var programArgs = new Arguments();
            if (!CommandLineParser.ParseWithUsage(args, programArgs))
            {
                return 1;
            }

            using (var program = new Program(programArgs))
            {
                return program.Execute() ? 0 : 1;
            }
        }

        public void Dispose()
        {
            _codeProvider?.Dispose();
        }

        private bool Execute()
        {
            var lines = File.ReadAllLines(_args.Path);
            var codeBlocks = FindCodeBlocks(lines).ToList();

            var result = true;
            foreach (var blockGroup in codeBlocks.GroupBy(
                b => !string.IsNullOrEmpty(b.Metadata.AssemblyId)
                    ? b.Metadata.AssemblyId
                    : Guid.NewGuid().ToString()))
            {
                if (_args.Verbose)
                {
                    Console.WriteLine("---------------------------------------------");
                }

                var blocks = blockGroup.ToList();

                var idString = blocks.First().Metadata.AssemblyId ?? "<anonymous>";

                if (!blocks.Any(b => b.Metadata.Compile))
                {
                    if (_args.Verbose)
                    {
                        Console.WriteLine($"Skipping disabled blocks with ID '{idString}': lines {string.Join(", ", blocks.Select(b => b.StartLineIndex))}");
                    }

                    continue;
                }

                if (!blocks.All(b => b.Language.Equals("csharp", StringComparison.OrdinalIgnoreCase)))
                {
                    Console.Error.WriteLine($"{_args.Path} : error MDC0003: Found unsupported language in blocks");
                    result = false;

                    continue;
                }

                if (_args.Verbose)
                {
                    Console.WriteLine($"Compiling code blocks with ID '{idString}': lines {string.Join(", ", blocks.Select(b => b.StartLineIndex))}");
                }

                if (!Compile(lines, blocks))
                {
                    result = false;
                }
            }

            if (_args.Verbose)
            {
                Console.WriteLine();
            }

            if (result)
            {
                if (_args.Verbose)
                {
                    Console.WriteLine("No failures occurred.");
                }
            }
            else
            {
                Console.Error.WriteLine($"{_args.Path} : error MDC0002: Failed to compile one or more code blocks");
            }

            return result;
        }

        private bool Compile(IEnumerable<string> lines, IEnumerable<CodeBlock> blocks)
        {
            var blocksList = blocks.ToList();
            var contentToCompile = blocksList.Select(b => GenerateSource(lines, b)).ToArray();

            if (_args.Verbose)
            {
                foreach (var block in blocksList)
                {
                    Console.WriteLine(
                        $"Block: start={block.StartLineIndex}, length={block.LineCount}, lang={block.Language ?? string.Empty}, compile={block.Metadata.Compile}");
                }

                Console.WriteLine();

                foreach (var c in contentToCompile)
                {
                    Console.WriteLine(c);
                }

                Console.WriteLine();
            }

            var parameters = new CompilerParameters(_args.ReferenceAssemblyPaths.Select(p => p.Path).ToArray())
            {
                GenerateInMemory = true,
                TreatWarningsAsErrors = true,
                GenerateExecutable = false
            };

            var results = _codeProvider.CompileAssemblyFromSource(parameters, contentToCompile);
            if (results.Errors.HasErrors || results.Errors.HasWarnings)
            {
	            var representativeBlock = blocksList.First();
	            var startLine = representativeBlock.StartLineIndex + 1;

                var idString = representativeBlock.Metadata.AssemblyId ?? "<anonymous>";

                Console.Error.WriteLine(
                    $"{_args.Path}({startLine},{1}) : error MDC0001: Failed to compile block for '{idString}' assembly");

                foreach (var message in results.Errors)
                {
                    Console.Error.WriteLine(message);
                }

                return false;
            }

            if (_args.Verbose)
            {
                Console.WriteLine("  Successfully compiled.");
            }

            return true;
        }

        private string GenerateSource(IEnumerable<string> lines, CodeBlock block)
        {
            var contentFromMd = GetContent(lines, block);

            var stringsToCompile = new List<string>();

            stringsToCompile.Add("#line 1 \"Inserted content\"");

            stringsToCompile.AddRange(block.Metadata.Imports.Select(i => $"using {i};"));

            if (block.Metadata.WrapInNamespace)
            {
                stringsToCompile.Add("namespace Test");
                stringsToCompile.Add("{");
            }

            if (block.Metadata.WrapInClass)
            {
                stringsToCompile.Add($"class Anonymous_{Guid.NewGuid().ToString().Replace("-", string.Empty)}");
                stringsToCompile.Add("{");
            }

            if (!string.IsNullOrEmpty(block.Metadata.Prefix))
            {
                stringsToCompile.Add(block.Metadata.Prefix);
            }

            stringsToCompile.Add($"#line {block.StartLineIndex + 1} \"{_args.Path}\"");

            stringsToCompile.Add(contentFromMd);

            stringsToCompile.Add("#line 1 \"Inserted content\"");

            if (block.Metadata.WrapInClass)
            {
                stringsToCompile.Add("}");
            }

            if (block.Metadata.WrapInNamespace)
            {
                stringsToCompile.Add("}");
            }

            stringsToCompile.Add("#line default");

            return string.Join(Environment.NewLine, stringsToCompile);
        }

        private static IEnumerable<CodeBlock> FindCodeBlocks(IReadOnlyList<string> lines)
        {
            CodeBlock currentCodeBlock = null;
            for (var i = 0; i < lines.Count; ++i)
            {
                const string codeBlockPrefix = "```";

                var trimmedLine = lines[i].Trim();
                if (!trimmedLine.StartsWith(codeBlockPrefix))
                {
                    continue;
                }

                if (currentCodeBlock != null)
                {
                    currentCodeBlock.LineCount = i - currentCodeBlock.StartLineIndex;
                    yield return currentCodeBlock;

                    currentCodeBlock = null;
                }
                else
                {
                    var metadata = new CodeBlockMetadata();

                    if (i > 0)
                    {
                        const string commentPrefix = "<!-- MdCompile: ";
                        const string commentSuffix = "-->";

                        var previousLine = lines[i - 1].Trim();
                        if (previousLine.StartsWith(commentPrefix, StringComparison.OrdinalIgnoreCase) &&
                            previousLine.EndsWith(commentSuffix, StringComparison.OrdinalIgnoreCase))
                        {
                            var parameters = previousLine.Substring(
                                commentPrefix.Length,
                                previousLine.Length - (commentPrefix.Length + commentSuffix.Length))
                                .Trim()
                                .Split(new[] {','}, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => "/" + s.Trim());

                            if (!CommandLineParser.Parse(parameters.ToList(), metadata))
                            {
                                throw new InvalidDataException($"Bad parameters: {previousLine}");
                            }
                        }
                    }

                    var restOfLine = trimmedLine.Substring(codeBlockPrefix.Length).Trim();
                    currentCodeBlock = new CodeBlock
                    {
                        StartLineIndex = i + 1,
                        Language = string.IsNullOrEmpty(restOfLine) ? null : restOfLine.ToLowerInvariant(),
                        Metadata = metadata
                    };
                }
            }

            if (currentCodeBlock != null)
            {
                currentCodeBlock.LineCount = lines.Count - currentCodeBlock.StartLineIndex;
                yield return currentCodeBlock;
            }
        }

        private static string GetContent(IEnumerable<string> lines, CodeBlock block) =>
            string.Join(Environment.NewLine, lines.Skip(block.StartLineIndex).Take(block.LineCount));
    }
}

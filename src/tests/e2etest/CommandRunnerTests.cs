// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using NUnit.Framework;
using System;

namespace E2eTesting
{
    [TestFixture, Category("CommandRunner")]
    public class CommandRunnerTests : E2ETest
    {
        private static readonly string _componentName = "CommandRunner";
        private static readonly string _desiredObjectName = "commandArguments";
        private static readonly string _reportedObjectName = "commandStatus";

        public enum Action
        {
            None = 0,
            Reboot,
            Shutdown,
            RunCommand,
            RefreshCommandStatus,
            CancelCommand
        }

        public enum CommandState
        {
            Unknown = 0,
            Running,
            Succeeded,
            Failed,
            TimedOut,
            Canceled
        }

        public class CommandArguments
        {
            public string CommandId { get; set; }
            public string Arguments { get; set; }
            public Action Action { get; set; }
            public int Timeout { get; set; }
            public bool SingleLineTextResult { get; set; }
        }

        public class CommandStatus
        {
            public string CommandId { get; set; }
            public long ResultCode { get; set; }
            public string TextResult { get; set; }
            public CommandState CurrentState { get; set; }
        }

        public static string GenerateId()
        {
            return Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Substring(0, 4);
        }

        public static CommandArguments CreateCommand(string arguments, Action action = Action.RunCommand, int timeout = 0, bool singleLineTextResult = false)
        {
            return CreateCommand(GenerateId(), arguments, action, timeout, singleLineTextResult);
        }

        public static CommandArguments CreateCommand(string commandId, string arguments, Action action = Action.RunCommand, int timeout = 0, bool singleLinetextResult = false)
        {
            return new CommandArguments
            {
                CommandId = commandId,
                Arguments = arguments,
                Action = action,
                Timeout = timeout,
                SingleLineTextResult = singleLinetextResult
            };
        }

        public static CommandArguments CreateLongRunningCommand(string commandId, int timeout = 120)
        {
            return new CommandArguments
            {
                CommandId = commandId,
                Arguments = "sleep 1000s",
                Action = Action.RunCommand,
                Timeout = timeout
            };
        }

        public static CommandArguments CreateCancelCommand(string commandId)
        {
            return new CommandArguments
            {
                CommandId = commandId,
                Arguments = "",
                Action = Action.CancelCommand,
                Timeout = 0
            };
        }

        public static CommandStatus CreateCommandStatus(string commandId, string textResult = "", CommandState commandState = CommandState.Succeeded, int resultCode = 0)
        {
            return new CommandStatus
            {
                CommandId = commandId,
                TextResult = textResult,
                CurrentState = commandState,
                ResultCode = resultCode
            };
        }

        public void SendCommand(CommandArguments command, int expectedAckCode = ACK_SUCCESS)
        {
            int ackCode = -1;

            Console.WriteLine($"{command.action} \"{command.commandId}\" ({command.arguments})");

            try
            {
                var setDesiredTask = SetDesired<CommandArguments>(_componentName, _desiredObjectName, command);
                setDesiredTask.Wait();
                ackCode = setDesiredTask.Result.ac;
            }
            catch (Exception e)
            {
                Assert.Fail("Failed to send command: {0}", e.Message);
            }

            if (ackCode != expectedAckCode)
            {
                Assert.Fail("CommandRunner.CommandArguments expected ackCode {0}, but got {1}", expectedAckCode, ackCode);
            }
        }

        public void CancelCommand(string commandId)
        {
            SendCommand(CreateCancelCommand(commandId));
        }

        public void RefreshCommandStatus(string commandId, int expectedAckCode = ACK_SUCCESS)
        {
            var refreshCommand = new CommandArguments
            {
                CommandId = commandId,
                Arguments = "",
                Action = Action.RefreshCommandStatus
            };

            SendCommand(refreshCommand, expectedAckCode);
        }

        public CommandStatus WaitForStatus(string commandId, CommandState state)
        {
            Func<CommandStatus, bool> condition = (CommandStatus status) => ((status.CommandId == commandId) && (status.CurrentState == state));
            var reportedTask = GetReported<CommandStatus>(_componentName, _reportedObjectName, condition);
            reportedTask.Wait();
            return reportedTask.Result;
        }

        [Test]
        [TestCase("echo 'hello world'", 0, false, 0, "hello world\n", CommandState.Succeeded)]
        [TestCase("sleep 10s", 1, true, 62, "", CommandState.TimedOut)]
        [TestCase("sleep 10s", 60, true, 0, "", CommandState.Succeeded)]
        [TestCase("echo 'single\nline'", 0, true, 0, "single line ", CommandState.Succeeded)]
        [TestCase("echo 'multiple\nlines'", 0, false, 0, "multiple\nlines\n", CommandState.Succeeded)]
        [TestCase("blah", 0, false, 127, "sh: 1: blah: not found\n", CommandState.Failed)]
        public void CommandRunnerTest_RunCommand(string arguments, int timeout, bool singleLineTextResult, int resultCode, string textResult, CommandState state)
        {
            var command = CreateCommand(arguments, Action.RunCommand, timeout, singleLineTextResult);
            SendCommand(command);

            CommandStatus status = WaitForStatus(command.CommandId, state);
            JsonAssert.AreEqual(CreateCommandStatus(command.CommandId, textResult, state, resultCode), status);
        }

        [Test]
        public void CommandRunnerTest_CancelCommand()
        {
            var commandId = GenerateId();
            var command = CreateLongRunningCommand(commandId);

            SendCommand(command);
            CommandStatus runningStatus = WaitForStatus(commandId, CommandState.Running);

            CancelCommand(command.CommandId);
            CommandStatus canceledStatus = WaitForStatus(commandId, CommandState.Canceled);

            Assert.Multiple(() =>
            {
                JsonAssert.AreEqual(CreateCommandStatus(commandId, "", CommandState.Running, 0), runningStatus);
                JsonAssert.AreEqual(CreateCommandStatus(commandId, "", CommandState.Canceled, 125), canceledStatus);
            });
        }

        [Test]
        public void CommandRunnerTest_RepeatCommandId()
        {
            var commandId = GenerateId();
            var command = CreateCommand(commandId, "echo 'command 1'", Action.RunCommand, 0, true);
            var commandWithDuplicateCommandId = CreateCommand(commandId, "echo 'command 2'", Action.RunCommand, 0, true);

            SendCommand(command);
            var commandStatus = WaitForStatus(commandId, CommandState.Succeeded);
            JsonAssert.AreEqual(CreateCommandStatus(commandId, "command 1 ", CommandState.Succeeded, 0), commandStatus);

            // Send a command with a duplicate command Id
            SendCommand(commandWithDuplicateCommandId, 400);
        }

        [Test]
        public void CommandRunnerTest_CommandSequence()
        {
            var command1 = CreateLongRunningCommand(GenerateId());
            var command2 = CreateCommand("echo 'command 2'");
            var command3 = CreateCommand("sleep 10s && echo 'command 3'", timeout: 1);
            var command4 = CreateCommand("blah");

            var expectedCommandStatus1 = CreateCommandStatus(command1.CommandId, "", CommandState.Canceled, 125);
            var expectedCommandStatus2 = CreateCommandStatus(command2.CommandId, "command 2\n", CommandState.Succeeded, 0);
            var expectedCommandStatus3 = CreateCommandStatus(command3.CommandId, "", CommandState.TimedOut, 62);
            var expectedCommandStatus4 = CreateCommandStatus(command4.CommandId, "sh: 1: blah: not found\n", CommandState.Failed, 127);

            SendCommand(command1);
            SendCommand(command2);
            SendCommand(command3);
            SendCommand(command4);
            SendCommand(CreateCancelCommand(command1.CommandId));

            // Wait for the last command to complete before checking all command statuses
            CommandStatus actualCommandStatus4 = WaitForStatus(command4.CommandId, CommandState.Failed);

            RefreshCommandStatus(command1.CommandId);
            CommandStatus actualCommandStatus1 = WaitForStatus(command1.CommandId, CommandState.Canceled);

            RefreshCommandStatus(command2.CommandId);
            CommandStatus actualCommandStatus2 = WaitForStatus(command2.CommandId, CommandState.Succeeded);

            RefreshCommandStatus(command3.CommandId);
            CommandStatus actualCommandStatus3 = WaitForStatus(command3.CommandId, CommandState.TimedOut);

            Assert.Multiple(() =>
            {
                JsonAssert.AreEqual(expectedCommandStatus1, actualCommandStatus1);
                JsonAssert.AreEqual(expectedCommandStatus2, actualCommandStatus2);
                JsonAssert.AreEqual(expectedCommandStatus3, actualCommandStatus3);
                JsonAssert.AreEqual(expectedCommandStatus4, actualCommandStatus4);
            });
        }
    }
}
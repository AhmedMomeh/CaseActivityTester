// The Intalio Case Designer shadows System.Threading.Tasks.Task with its DAL Task
// entity for legacy reasons. We mirror that here so user activity files compile
// unchanged in this test host. If an activity legitimately uses the DAL Task type,
// it must reference it as Intalio.Case.Portal.Core.DAL.Task explicitly.
global using Task = System.Threading.Tasks.Task;

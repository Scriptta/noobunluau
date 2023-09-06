# Unluau CLI
The Unluau CLI (command line interface) makes it extremely easy to interact with the decompiler without writing any code at all. As of now the CLI is availible for Linux and Windows operating systems. To see if your operating system is supported, check the [releases](https://github.com/valencefun/unluau/tags) tab for a matching binary. 

Once you have located the appropriate binary you can run it by passing it a luau binary file. If you are unfamilliar with luau bytecode generation or simply don't know how to generate it, check out our luau workspace [here](https://github.com/valencefun/luau-workspace).

## Options
The command line interface has a plethora of options availble to customize the behavior of the decompiler. From showing the bare luau bytecode instructions to variable name guessing unluau has it all. Refer to the table of contents below:
* [Input](#input)
* [Output](#output--o---output)
* [Dissasemble](#dissasemble--d---dissasemble)
* [Verbose](#verbose--v---verbose)
* [Supress Warnings](#supress-warnings---supress-warnings)
* [Logs](#logs---logs)
* [Inline Tables](#inline-tables---inline-tables)
* [Rename Upvalues](#rename-upvalues---rename-upvalues)

### Input
A single, optional, argument that determines the source of the bytecode to decompile. To decompile a file your command should look something like this:
```
unluau <inputfile.luau>
```
If you don't end up providing an input file, you will need to provide it via `stdin` (standard input).

### Output `-o, --output`
You can direct the output of the decompiler to a file using ``-o`` or `--output`. If this option is not used then the output will just go to stdout (standard out). An example of this option in use can be found below:
```
unluau inputfile.luau -o outputfile.lua
```

### Dissasemble `-d, --dissasemble`
When provided, this option will print a dissasembled version of the "assembled" luau machine code to standard out. In simple words it converts the machine code to a somewhat readable format. For example, lets say we have the following script compiled in `Closure.luau`:
```lua
local function Closure()
    print(1)
end

print(Closure)
```
The dissasembled output for the script would be the following:
```
0 param(s), 2 slot(s), 0 upvalue(s), 2 constant(s), 0 function(s)
function Closure() -- line 1 through 2
   GETIMPORT         0 1
      AUX        1073741824
   LOADN             1 1
   CALL              0 2 1
   RETURN            0 1 0
end

0+ param(s), 3 slot(s), 0 upvalue(s), 3 constant(s), 1 function(s)
function main(...) -- line 1 through 5
   PREPVARARGS       0 0 0
   DUPCLOSURE        0 0
   GETIMPORT         1 2
      AUX        1074790400
   MOVE              2 0 0
   CALL              1 2 1
   RETURN            0 1 0
end

Main Function: 1
```
The dissasembled output shows us information about each function defined in the script. We get to see instruction data (operation code and operands), line info, debug information (function names, etc.), registers, upvalues, and constants. This feature is most useful to us for debugging so you shouldn't expect to use this option if you are here to decompile scripts.

### Verbose `-v, --verbose`
If provided Unluau will enter a verbose mode and will display additional information about the decompilation process. In specific, logs will be written to a desired output stream. This option is most useful for debugging and not something you should be using often.

### Supress Warnings `--supress-warnings`
If the decompiler is in verbose mode and this option is provided, warning logs will not be written to the output stream. It is recommended that you use this option only when needed as warning messages usually contain vital information.

### Logs `--logs`
This option specifies the output stream for the decompilation logs. If this option is not specified then the logs will get printed to standard out, otherwise they will be written to the specified file.

### Inline Tables `--inline-tables`
Tells the decompiler to inline table definitions. Naturally the decompiler has no way of knowing if the initial values of have been assigned via individual assignment or the table constructor `{}`. By enabling this option the decompiler will prioritize the table constructor over individual assignments.

The script below is an example of a script decompiled without the flag:
```lua
local var0 = {}
var0[1] = 1
var0[2] = 2
var0[3] = 3
var0[4] = 4
```
And now with the flag enabled:
```lua
local var0 = { 1, 2, 3, 4 }
```

### Rename Upvalues `--rename-upvalues`
Renames local variables that are defined outside of the closure that they are being referenced in. Unluau will rename them to `upvalue{x}` to help distinguish from regular variables.
```lua
local upvalue0 = 1

local function f()
    return upvalue0
end
```

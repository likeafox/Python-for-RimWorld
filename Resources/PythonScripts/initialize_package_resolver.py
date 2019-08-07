"Set up Python-for-RimWorld's special package resolution"

##### imp.load_module doc #####
# source found in:
# ironpython2-ipy-2.7.7\Languages\IronPython\IronPython\Modules\imp.cs
# 
# POSSIBLE CALL SIGNATURES:
# source:
# >>> imp.load_module("name", file_handle, "D:/dir/filename.py", (None,None,imp.PY_SOURCE))
# built-ins:
# >>> imp.load_module("name", None, None, (None,None,imp.C_BUILTIN))
# package directory:
# >>> imp.load_module("name", None, "D:/dir/", (None,None,imp.PKG_DIRECTORY))
#
# Unlike the much less useful imp.find_module, imp.load_module's `name`
# may have "." in it.
#
# more reading:
# https://docs.python.org/2.7/library/imp.html#imp.load_module
#

##### inspect.stack doc #####
#each record returned is a tuple of six items:
# 0 the frame object
# 1 the filename (actually full file path)
# 2 the line number of the current line
# 3 the function name
# 4 a list of lines of context from the source code
# 5 and the index of the current line within that list.
#

## References to understand importing:
# https://docs.python.org/2/reference/simple_stmts.html#the-import-statement
# https://www.python.org/dev/peps/pep-0302/#specification-part-1-the-importer-protocol

import imp, inspect, Python, Verse, sys, os.path
find_mod_for_file = Python.PythonModManager.FindModOfFilesystemObject

class SourceModuleLoader(object):
    def __init__ (self, name, absname, path):
        self.name = name
        self.absname = absname
        self.path = path

    def load_module(self, fullname):
        if fullname != self.name:
            raise ImportError('Custom module loader expected "' +
                self.name + '" but got "' +
                fullname + '"')
        try:
            return sys.modules[fullname]
        except:
            return self.load_action()

    def load_action(self):
        with open(self.path) as f:
            desc = (None,None,imp.PY_SOURCE)
            return imp.load_module(self.absname, f, self.path, desc)

class PackageModuleLoader(SourceModuleLoader):
    def load_action(self):
        desc = (None,None,imp.PKG_DIRECTORY)
        return imp.load_module(self.absname, None, self.path, desc)

class UserModuleFinder(object):
    def find_module(self, fullname, path=None):
        if fullname[0] == '.':
            return #not supporting relative modules yet
        
        #1. check if it's a local import to a mod
        try:
            invoker_file = inspect.stack()[1][1]
        except Exception as e:
            Verse.Log.Warning(str(e))
            return None
        mod = find_mod_for_file(invoker_file)
        if mod:
            # we're in a mod so it could be.
            absfullname_parts = [mod.ModuleName] + fullname.split('.')
            absfullname = '.'.join(absfullname_parts)
            # Normally you'd call get_suffixes() here to be compliant but
            #we know IronPython only returns .py
            name = absfullname_parts[-1]
            filename = name + ".py"
            folder = name + "/"
            absparent = '.'.join(absfullname_parts[:-1])
            try:
                parentpaths = sys.modules[absparent].__path__
            except:
                pass
            else:
                for p in parentpaths:
                    try:
                        _, dirs, files = os.walk(p).next()
                    except Exception as e:
                        Verse.Log.Error(e)
                        return None
                    if filename in files:
                        filepath = os.path.join(p,filename)
                        return SourceModuleLoader(fullname, absfullname, filepath)
                    if name is dirs:
                        dirpath = os.path.join(p,folder)
                        return PackageModuleLoader(fullname, absfullname, filepath)

        #2. check if it's in our own module catalogue
        #NOT IMPLEMENTED
        return None

sys.meta_path.append(UserModuleFinder())

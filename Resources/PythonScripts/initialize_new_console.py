import sys
import code
from code import compile_command
import io
from contextlib import contextmanager
output_buffer = io.StringIO()
sys.stdout = sys.stderr = output_buffer
interpreter = code.InteractiveInterpreter(_locals)
backup_fds = []
@contextmanager
def redirect_output(fd):
    backup_fds.append((sys.stdout, sys.stderr))
    sys.stdout, sys.stderr = fd, fd
    yield
    sys.stdout, sys.stderr = backup_fds.pop()
def console_run_code(code):
    with redirect_output(output_buffer):
        return interpreter.runcode(code)
